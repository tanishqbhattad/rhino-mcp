"""
pdf_tracer.py — High-accuracy PDF-to-Rhino geometry pipeline for RhinoAIBridge v4.7

Extracts lines, arcs, polylines and text from architectural PDF drawings,
returning structured JSON for TracingManager.cs to consume.

Dependencies:
  pip install pymupdf opencv-python numpy
"""

import base64
import math
import os

try:
    import numpy as np
    _HAS_NUMPY = True
except ImportError:
    _HAS_NUMPY = False

try:
    import fitz  # PyMuPDF
    _HAS_FITZ = True
except ImportError:
    _HAS_FITZ = False

try:
    import cv2
    _HAS_CV2 = True
except ImportError:
    _HAS_CV2 = False

# ---------------------------------------------------------------------------
# Unit conversion
# ---------------------------------------------------------------------------
_UNIT_TO_MM = {"mm": 1.0, "cm": 10.0, "m": 1000.0, "ft": 304.8, "in": 25.4}


def _px_to_model(px, py, img_height_px, dpi, model_unit="mm"):
    """Pixel → model units; flips Y so origin is bottom-left."""
    factor = (25.4 / dpi) / _UNIT_TO_MM.get(model_unit, 1.0)
    return px * factor, (img_height_px - py) * factor


# ---------------------------------------------------------------------------
# Geometry helpers
# ---------------------------------------------------------------------------

def _least_squares_circle(pts):
    """Algebraic least-squares circle fit. Returns (cx, cy, r) or None."""
    if not _HAS_NUMPY or len(pts) < 3:
        return None
    pts = np.asarray(pts, dtype=np.float64)
    A = np.c_[2 * pts[:, 0], 2 * pts[:, 1], np.ones(len(pts))]
    b = pts[:, 0] ** 2 + pts[:, 1] ** 2
    try:
        result, _, _, _ = np.linalg.lstsq(A, b, rcond=None)
    except Exception:
        return None
    cx, cy = result[0], result[1]
    r = math.sqrt(max(0.0, result[2] + cx ** 2 + cy ** 2))
    return cx, cy, r


def _merge_collinear_segments(segments, angle_tol_deg=2.0, dist_tol_px=5.0):
    """Merge overlapping / nearly-collinear segments.
    Each segment tuple: (x1, y1, x2, y2, confidence).
    """
    if not segments:
        return []

    def seg_angle(s):
        a = math.degrees(math.atan2(s[3] - s[1], s[2] - s[0])) % 180.0
        return a

    def seg_line_dist(s):
        dx, dy = s[2] - s[0], s[3] - s[1]
        length = math.hypot(dx, dy)
        if length < 1e-6:
            return math.hypot(s[0], s[1])
        nx, ny = -dy / length, dx / length
        return s[0] * nx + s[1] * ny

    def project_t(px, py, s):
        dx, dy = s[2] - s[0], s[3] - s[1]
        length = math.hypot(dx, dy)
        if length < 1e-6:
            return 0.0
        return ((px - s[0]) * dx + (py - s[1]) * dy) / length

    merged = []
    used = [False] * len(segments)

    for i, si in enumerate(segments):
        if used[i]:
            continue
        ai = seg_angle(si)
        di = seg_line_dist(si)
        group = [si]

        for j, sj in enumerate(segments):
            if i == j or used[j]:
                continue
            aj = seg_angle(sj)
            diff = abs(ai - aj) % 180.0
            if diff > 90:
                diff = 180.0 - diff
            if diff > angle_tol_deg:
                continue
            if abs(di - seg_line_dist(sj)) > dist_tol_px:
                continue
            group.append(sj)
            used[j] = True
        used[i] = True

        dx = si[2] - si[0];  dy = si[3] - si[1]
        length = math.hypot(dx, dy)
        if length < 1e-6:
            merged.append(si)
            continue
        ux, uy = dx / length, dy / length
        projs = []
        for s in group:
            projs.extend([project_t(s[0], s[1], si), project_t(s[2], s[3], si)])
        t_min, t_max = min(projs), max(projs)
        # Merge if the group spans <= sum of individual lengths + gap tolerance
        # (i.e. segments overlap or are close together on the axis)
        total_individual = sum(math.hypot(s[2]-s[0], s[3]-s[1]) for s in group)
        if t_max - t_min <= total_individual + dist_tol_px * len(group):
            x1n = si[0] + ux * t_min;  y1n = si[1] + uy * t_min
            x2n = si[0] + ux * t_max;  y2n = si[1] + uy * t_max
            avg_conf = sum(s[4] for s in group) / len(group)
            merged.append((x1n, y1n, x2n, y2n, avg_conf))
        else:
            merged.extend(group)

    return merged


# ---------------------------------------------------------------------------
# Main entry point
# ---------------------------------------------------------------------------

def trace_pdf(
    pdf_path,
    page_number=0,
    dpi=300,
    model_unit="mm",
    confidence_threshold=0.5,
    merge_tolerance_px=5.0,
    min_line_length_px=10.0,
):
    """
    Trace a PDF page and return structured JSON for TracingManager.ApplyTracedElements.
    Returns: {"elements": [...], "metadata": {...}}
    """
    missing = []
    if not _HAS_FITZ:
        missing.append("pymupdf (pip install pymupdf)")
    if not _HAS_CV2:
        missing.append("opencv-python (pip install opencv-python)")
    if not _HAS_NUMPY:
        missing.append("numpy (pip install numpy)")
    if missing:
        return {"error": "Missing: " + ", ".join(missing), "elements": [], "metadata": {}}

    if not os.path.isfile(pdf_path):
        return {"error": f"File not found: {pdf_path}", "elements": [], "metadata": {}}

    elements = []
    is_scanned = False
    img_h = img_w = 0

    try:
        doc = fitz.open(pdf_path)
        if page_number >= len(doc):
            return {
                "error": f"Page {page_number} out of range ({len(doc)} pages)",
                "elements": [], "metadata": {}
            }
        page = doc[page_number]
        page_h_pt = page.rect.height

        # Render to grayscale
        mat = fitz.Matrix(dpi / 72.0, dpi / 72.0)
        pix = page.get_pixmap(matrix=mat, colorspace=fitz.csGRAY, alpha=False)
        img_h, img_w = pix.height, pix.width
        gray = np.frombuffer(pix.samples, dtype=np.uint8).reshape(img_h, img_w).copy()

        # Vector text (perfect accuracy, no CV needed)
        elements.extend(_extract_vector_text(page, page_h_pt, img_h, dpi, model_unit))

        # Is this a scanned PDF?
        is_scanned = len(page.get_drawings()) < 5

        doc.close()
    except Exception as ex:
        return {"error": f"PDF load failed: {ex}", "elements": elements, "metadata": {}}

    try:
        binary = _preprocess(gray)

        # Lines
        raw_lines = _detect_lines(gray, binary, min_line_length_px)
        merged = _merge_collinear_segments(raw_lines, 2.0, merge_tolerance_px)
        conf_scale = 0.85 if is_scanned else 1.0
        for (x1, y1, x2, y2, conf) in merged:
            mx1, my1 = _px_to_model(x1, y1, img_h, dpi, model_unit)
            mx2, my2 = _px_to_model(x2, y2, img_h, dpi, model_unit)
            elements.append({
                "type": "line",
                "x1": round(mx1, 4), "y1": round(my1, 4),
                "x2": round(mx2, 4), "y2": round(my2, 4),
                "confidence": round(max(0.0, min(1.0, conf * conf_scale)), 3),
            })

        # Arcs
        elements.extend(_detect_arcs(gray, binary, img_h, dpi, model_unit, is_scanned))

        # Polylines
        elements.extend(_detect_polylines(binary, raw_lines, img_h, dpi, model_unit, is_scanned))

    except Exception as ex:
        elements.append({"type": "_error", "message": f"CV pipeline: {ex}"})

    factor = (25.4 / dpi) / _UNIT_TO_MM.get(model_unit, 1.0)
    counts = {t: sum(1 for e in elements if e.get("type") == t)
              for t in ("line", "arc", "polyline", "text")}
    metadata = {
        "source_file": os.path.basename(pdf_path),
        "page_number": page_number,
        "dpi": dpi,
        "model_unit": model_unit,
        "page_width_px": img_w,
        "page_height_px": img_h,
        "page_width_model": round(img_w * factor, 4),
        "page_height_model": round(img_h * factor, 4),
        "is_scanned": is_scanned,
        "counts": counts,
    }
    return {"elements": elements, "metadata": metadata}


# ---------------------------------------------------------------------------
# Preprocessing
# ---------------------------------------------------------------------------

def _preprocess(gray):
    if np.mean(gray) < 128:
        gray = 255 - gray  # invert dark backgrounds
    blurred = cv2.GaussianBlur(gray, (3, 3), 0)
    binary = cv2.adaptiveThreshold(
        blurred, 255,
        cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        cv2.THRESH_BINARY_INV,
        blockSize=15, C=5,
    )
    kernel = np.ones((2, 2), np.uint8)
    return cv2.morphologyEx(binary, cv2.MORPH_CLOSE, kernel)


# ---------------------------------------------------------------------------
# Line detection
# ---------------------------------------------------------------------------

def _detect_lines(gray, binary, min_len):
    lines_raw = cv2.HoughLinesP(
        binary, rho=1, theta=np.pi / 360,
        threshold=30, minLineLength=min_len, maxLineGap=8,
    )
    if lines_raw is None:
        return []
    result = []
    for ln in lines_raw:
        x1, y1, x2, y2 = ln[0]
        conf = _line_confidence(binary, x1, y1, x2, y2)
        result.append((float(x1), float(y1), float(x2), float(y2), conf))
    return result


def _line_confidence(binary, x1, y1, x2, y2):
    length = math.hypot(x2 - x1, y2 - y1)
    if length < 1e-3:
        return 0.0
    n = max(5, int(length / 3))
    h, w = binary.shape
    hits = 0
    for i in range(n):
        t = i / (n - 1)
        px = int(round(x1 + t * (x2 - x1)))
        py = int(round(y1 + t * (y2 - y1)))
        if 0 <= px < w and 0 <= py < h and binary[py, px] > 0:
            hits += 1
    straightness = hits / n
    length_bonus = min(1.0, length / 200.0)
    return round(0.65 * straightness + 0.35 * length_bonus, 3)


# ---------------------------------------------------------------------------
# Arc detection
# ---------------------------------------------------------------------------

def _detect_arcs(gray, binary, img_h, dpi, model_unit, is_scanned):
    elements = []
    conf_scale = 0.85 if is_scanned else 1.0
    factor = (25.4 / dpi) / _UNIT_TO_MM.get(model_unit, 1.0)

    # Hough circles
    blurred = cv2.GaussianBlur(gray, (5, 5), 0)
    circles = cv2.HoughCircles(
        blurred, cv2.HOUGH_GRADIENT, dp=1, minDist=20,
        param1=50, param2=30, minRadius=5, maxRadius=min(gray.shape) // 2,
    )
    if circles is not None:
        for (cx, cy, r) in np.round(circles[0]).astype(int):
            conf = _circle_confidence(binary, cx, cy, r) * conf_scale
            if conf < 0.3:
                continue
            mx, my = _px_to_model(float(cx), float(cy), img_h, dpi, model_unit)
            mr = r * factor
            elements.append({
                "type": "arc",
                "cx": round(mx, 4), "cy": round(my, 4), "r": round(mr, 4),
                "start_angle_deg": 0.0, "end_angle_deg": 360.0,
                "confidence": round(max(0.0, min(1.0, conf)), 3),
            })

    # Contour arc fitting
    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    for cnt in contours:
        if len(cnt) < 12:
            continue
        pts = cnt.reshape(-1, 2).astype(np.float64)
        fit = _least_squares_circle(pts)
        if fit is None:
            continue
        cx, cy, r = fit
        if r < 5 or r > min(gray.shape) / 2:
            continue
        conf = _circle_confidence(binary, int(cx), int(cy), int(r)) * conf_scale
        if conf < 0.4:
            continue
        # Negate Y component to account for image-space Y-down -> model-space Y-up flip
        angles = np.degrees(np.arctan2(-(pts[:, 1] - cy), pts[:, 0] - cx))
        a_min, a_max = float(np.min(angles)), float(np.max(angles))
        if a_max - a_min < 20:
            continue
        mx, my = _px_to_model(cx, cy, img_h, dpi, model_unit)
        mr = r * factor
        elements.append({
            "type": "arc",
            "cx": round(mx, 4), "cy": round(my, 4), "r": round(mr, 4),
            "start_angle_deg": round(a_min, 2), "end_angle_deg": round(a_max, 2),
            "confidence": round(max(0.0, min(1.0, conf)), 3),
        })

    return elements


def _circle_confidence(binary, cx, cy, r, n=72):
    h, w = binary.shape
    hits = 0
    for i in range(n):
        a = 2 * math.pi * i / n
        px = int(round(cx + r * math.cos(a)))
        py = int(round(cy + r * math.sin(a)))
        if 0 <= px < w and 0 <= py < h and binary[py, px] > 0:
            hits += 1
    return hits / n


# ---------------------------------------------------------------------------
# Polyline detection
# ---------------------------------------------------------------------------

def _detect_polylines(binary, raw_lines, img_h, dpi, model_unit, is_scanned):
    elements = []
    conf_scale = 0.85 if is_scanned else 1.0
    mask = binary.copy()
    for (x1, y1, x2, y2, _) in raw_lines:
        cv2.line(mask, (int(x1), int(y1)), (int(x2), int(y2)), 0, thickness=3)

    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    for cnt in contours:
        if len(cnt) < 4:
            continue
        if cv2.contourArea(cnt) < 25:
            continue
        peri = cv2.arcLength(cnt, closed=True)
        approx = cv2.approxPolyDP(cnt, epsilon=2.0, closed=True)
        if len(approx) < 3:
            continue
        conf = min(1.0, cv2.arcLength(approx, True) / max(1.0, peri)) * conf_scale
        pts = approx.reshape(-1, 2)
        model_pts = []
        for (px, py) in pts:
            mx, my = _px_to_model(float(px), float(py), img_h, dpi, model_unit)
            model_pts.append([round(mx, 4), round(my, 4)])
        elements.append({
            "type": "polyline",
            "points": model_pts,
            "closed": len(approx) >= 4,
            "confidence": round(max(0.0, min(1.0, conf)), 3),
        })
    return elements


# ---------------------------------------------------------------------------
# Vector text extraction
# ---------------------------------------------------------------------------

def _extract_vector_text(page, page_h_pt, img_h, dpi, model_unit):
    elements = []
    try:
        text_dict = page.get_text("dict")
    except Exception:
        return elements
    scale = dpi / 72.0
    for block in text_dict.get("blocks", []):
        if block.get("type") != 0:
            continue
        for line in block.get("lines", []):
            for span in line.get("spans", []):
                text = span.get("text", "").strip()
                if not text:
                    continue
                ox, oy = span["origin"]
                # PyMuPDF origin is top-left, Y increases downward — same as image coords
                px = ox * scale
                py = oy * scale  # PyMuPDF origin is top-left, Y increases downward — same as image coords
                mx, my = _px_to_model(px, py, img_h, dpi, model_unit)
                size_pt = span.get("size", 12.0)
                height_m = size_pt * (25.4 / 72.0) / _UNIT_TO_MM.get(model_unit, 1.0)
                d = line.get("dir", (1.0, 0.0))
                angle = math.degrees(math.atan2(d[1], d[0]))
                elements.append({
                    "type": "text",
                    "text": text,
                    "x": round(mx, 4), "y": round(my, 4),
                    "height": round(max(0.5, height_m), 4),
                    "angle_deg": round(angle, 2),
                    "confidence": 0.7 if size_pt < 6 else 1.0,
                })
    return elements


# ---------------------------------------------------------------------------
# Utility
# ---------------------------------------------------------------------------

def get_pdf_info(pdf_path):
    """Return page count, sizes, and vector content flag."""
    if not _HAS_FITZ:
        return {"error": "pymupdf not installed. Run: pip install pymupdf"}
    if not os.path.isfile(pdf_path):
        return {"error": f"File not found: {pdf_path}"}
    try:
        doc = fitz.open(pdf_path)
        pages = []
        for i, p in enumerate(doc):
            r = p.rect
            pages.append({
                "page": i,
                "width_mm": round(r.width * 25.4 / 72, 2),
                "height_mm": round(r.height * 25.4 / 72, 2),
                "has_vector_geometry": len(p.get_drawings()) > 5,
                "has_text": len(p.get_text().strip()) > 0,
            })
        doc.close()
        return {"page_count": len(pages), "pages": pages}
    except Exception as ex:
        return {"error": str(ex)}


def render_page_preview(pdf_path, page_number=0, max_size=800):
    """Render page thumbnail, return base64 PNG string (or None on error)."""
    if not _HAS_FITZ or not os.path.isfile(pdf_path):
        return None
    try:
        doc = fitz.open(pdf_path)
        page = doc[min(page_number, len(doc) - 1)]
        r = page.rect
        scale = min(max_size / max(r.width, r.height), 2.0)
        pix = page.get_pixmap(matrix=fitz.Matrix(scale, scale), alpha=False)
        doc.close()
        return base64.b64encode(pix.tobytes("png")).decode("ascii")
    except Exception:
        return None
