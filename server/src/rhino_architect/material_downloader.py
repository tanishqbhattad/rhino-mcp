import os
import json
import urllib.request
import urllib.parse
import shutil
import tempfile
import zipfile
import fnmatch
from pathlib import Path

CACHE_DIR = Path(os.environ.get("APPDATA", "")) / "AIBridge" / "materials"

_BASE_URL = "https://ambientcg.com/api/v2/full_json"

# Resolution preference order - first match wins
_RESOLUTION_PREFERENCE = [
    "2K-PNG", "2K-JPG", "1K-PNG", "1K-JPG",
    "4K-PNG", "4K-JPG", "512-PNG", "512-JPG",
]

# Map patterns to texture slot names
_MAP_PATTERNS = {
    "albedo":       ["*_Color*", "*_AmbientColor*"],
    "roughness":    ["*_Roughness*"],
    "normal":       ["*_NormalGL*", "*_Normal*"],
    "metallic":     ["*_Metalness*"],
    "ao":           ["*_AmbientOcclusion*", "*_ao*"],
    "displacement": ["*_Displacement*"],
}


def _fetch_json(url: str) -> dict:
    """Fetch a URL and return parsed JSON."""
    req = urllib.request.Request(url, headers={"User-Agent": "RhinoAIBridge/4.7.5"})
    with urllib.request.urlopen(req, timeout=15) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _extract_downloads(asset: dict) -> list[dict]:
    """Extract download entries from an asset, handling both old and new API formats.

    New format: downloadFolders.default.downloadFiletypeCategories.zip.downloads
    Old format: downloadFolders.default.downloadFileArray
    """
    folder = asset.get("downloadFolders", {}).get("default", {})

    # New API path (2025+)
    categories = folder.get("downloadFiletypeCategories", {})
    if categories:
        zip_cat = categories.get("zip", {})
        downloads = zip_cat.get("downloads", [])
        if downloads:
            return downloads

    # Old API path (fallback)
    old_array = folder.get("downloadFileArray", [])
    if old_array:
        # Normalize old format keys to new format
        normalized = []
        for entry in old_array:
            normalized.append({
                "attribute": entry.get("downloadAttribute", entry.get("attribute", "")),
                "fullDownloadPath": entry.get("fullDownloadPath", ""),
            })
        return normalized

    return []


def _extract_resolutions(downloads: list[dict]) -> list[str]:
    """Get list of available resolution strings from download entries."""
    return [d.get("attribute", "") for d in downloads if d.get("attribute")]


def _extract_preview_url(asset: dict) -> str:
    """Extract best preview image URL from asset.

    New API uses 'previewImage' dict. Old API used 'previewData' dict.
    """
    # New API: previewImage (full URLs when using id= or imageData include)
    preview_image = asset.get("previewImage", {})
    if isinstance(preview_image, dict) and preview_image:
        for key in ("512-PNG", "256-PNG", "128-PNG", "64-PNG"):
            url = preview_image.get(key, "")
            if url and "/media/thumbnail/" in url:
                return url
        # Return first non-empty full URL
        for v in preview_image.values():
            if v and isinstance(v, str) and "/media/thumbnail/" in v:
                return v

    # Old API: previewData
    preview_data = asset.get("previewData", {})
    if isinstance(preview_data, dict) and preview_data:
        for key in ("512-PNG", "256-PNG"):
            if key in preview_data:
                return preview_data[key]
        vals = list(preview_data.values())
        if vals:
            return vals[0]

    # Construct from asset ID pattern (reliable fallback)
    asset_id = asset.get("assetId", "")
    if asset_id:
        return "https://acg-media.struffelproductions.com/file/ambientCG-Web/media/photo/{}/{}_Photo.jpg".format(asset_id, asset_id)

    return ""


def _extract_dimensions(asset: dict) -> float:
    """Extract physical size in meters. Returns 1.0 as fallback."""
    dims = asset.get("dimensionsInMeters")
    if dims and isinstance(dims, (list, tuple)) and len(dims) > 0:
        val = dims[0]
        if val and isinstance(val, (int, float)) and val > 0:
            return float(val)
    return 1.0


def search_materials(keyword: str, limit: int = 5) -> list[dict]:
    """Search AmbientCG for PBR material assets matching keyword.

    Returns a list of candidate dicts with keys:
      asset_id, display_name, physical_size_m, resolutions_available, preview_url
    """
    params = urllib.parse.urlencode({
        "type": "Material",
        "include": "downloadData,imageData",
        "sort": "Popular",
        "q": keyword,
        "limit": limit,
    })
    url = "{}?{}".format(_BASE_URL, params)
    data = _fetch_json(url)

    results = []
    for asset in data.get("foundAssets", []):
        downloads = _extract_downloads(asset)
        resolutions = _extract_resolutions(downloads)
        preview_url = _extract_preview_url(asset)
        physical_size_m = _extract_dimensions(asset)

        results.append({
            "asset_id": asset.get("assetId", ""),
            "display_name": asset.get("displayName", ""),
            "physical_size_m": physical_size_m,
            "resolutions_available": resolutions,
            "preview_url": preview_url,
        })

    return results


def get_material_info(asset_id: str) -> dict:
    """Get full info for a specific asset including download URLs and real-world dimensions.

    Uses the id= parameter for exact asset lookup (not q= which is text search).
    """
    params = urllib.parse.urlencode({
        "include": "downloadData,imageData",
        "id": asset_id,
    })
    url = "{}?{}".format(_BASE_URL, params)
    data = _fetch_json(url)

    assets = data.get("foundAssets", [])
    if not assets:
        # Fallback: try text search
        params2 = urllib.parse.urlencode({
            "type": "Material",
            "include": "downloadData,imageData",
            "q": asset_id,
            "limit": 5,
        })
        url2 = "{}?{}".format(_BASE_URL, params2)
        data2 = _fetch_json(url2)
        assets = data2.get("foundAssets", [])
        if not assets:
            return {}
        # Find exact match
        for a in assets:
            if a.get("assetId", "").lower() == asset_id.lower():
                return a
        return assets[0]

    # Find exact match
    for asset in assets:
        if asset.get("assetId", "").lower() == asset_id.lower():
            return asset

    return assets[0]


def _pick_download_entry(downloads: list[dict], resolution: str) -> dict | None:
    """Find the best matching download entry for the requested resolution."""
    pref_attr = "{}-PNG".format(resolution)
    pref_attr_jpg = "{}-JPG".format(resolution)

    ordered = [pref_attr, pref_attr_jpg] + [
        p for p in _RESOLUTION_PREFERENCE
        if p not in (pref_attr, pref_attr_jpg)
    ]

    attr_map = {
        entry.get("attribute", ""): entry
        for entry in downloads
        if entry.get("attribute")
    }

    for attr in ordered:
        if attr in attr_map:
            return attr_map[attr]

    return downloads[0] if downloads else None


def _map_files_to_slots(extracted_files: list[Path]) -> dict[str, str]:
    """Match extracted image files to PBR map slots by filename patterns."""
    slots = {}
    image_exts = {".png", ".jpg", ".jpeg", ".tif", ".tiff", ".exr"}

    for slot, patterns in _MAP_PATTERNS.items():
        if slot in slots:
            continue
        for pattern in patterns:
            for f in extracted_files:
                if f.suffix.lower() in image_exts and fnmatch.fnmatch(f.name, pattern):
                    slots[slot] = str(f)
                    break
            if slot in slots:
                break

    return slots


def download_material(asset_id: str, resolution: str = "2K") -> dict:
    """Download PBR texture maps for an asset.

    Returns dict with:
      asset_id, display_name, physical_size_m, local_paths, resolution_used, already_cached
    """
    cache_dir = CACHE_DIR / asset_id
    cache_dir.mkdir(parents=True, exist_ok=True)

    # Check cache - if any image files exist, treat as cached
    existing_images = [
        f for f in cache_dir.rglob("*")
        if f.is_file() and f.suffix.lower() in {".png", ".jpg", ".jpeg", ".exr", ".tiff", ".tif"}
    ]

    info = get_material_info(asset_id)
    physical_size_m = _extract_dimensions(info) if info else 1.0
    display_name = info.get("displayName", asset_id) if info else asset_id

    if existing_images:
        local_paths = _map_files_to_slots(existing_images)
        return {
            "asset_id": asset_id,
            "display_name": display_name,
            "physical_size_m": physical_size_m,
            "local_paths": local_paths,
            "resolution_used": resolution,
            "already_cached": True,
        }

    # Find download entries using new API path
    downloads = _extract_downloads(info) if info else []

    if not downloads:
        # Last resort: construct direct URL from known pattern
        direct_url = "https://ambientcg.com/get?file={}_{}-JPG.zip".format(asset_id, resolution)
        downloads = [{"attribute": "{}-JPG".format(resolution), "fullDownloadPath": direct_url}]

    entry = _pick_download_entry(downloads, resolution)
    if not entry:
        raise ValueError("No suitable download entry found for asset {} at {}".format(asset_id, resolution))

    download_url = entry.get("fullDownloadPath", "")
    resolution_used = entry.get("attribute", resolution)

    if not download_url:
        # Construct from pattern
        download_url = "https://ambientcg.com/get?file={}_{}.zip".format(asset_id, resolution_used)

    # Download zip to temp file
    with tempfile.NamedTemporaryFile(suffix=".zip", delete=False) as tmp:
        tmp_path = Path(tmp.name)

    try:
        req = urllib.request.Request(download_url, headers={"User-Agent": "RhinoAIBridge/4.7.5"})
        with urllib.request.urlopen(req, timeout=60) as resp, open(tmp_path, "wb") as out:
            shutil.copyfileobj(resp, out)

        # Extract zip
        with zipfile.ZipFile(tmp_path, "r") as zf:
            zf.extractall(cache_dir)

    finally:
        if tmp_path.exists():
            tmp_path.unlink()

    # Collect extracted image files
    extracted = [
        f for f in cache_dir.rglob("*")
        if f.is_file() and f.suffix.lower() in {".png", ".jpg", ".jpeg", ".tif", ".tiff", ".exr"}
    ]

    local_paths = _map_files_to_slots(extracted)

    return {
        "asset_id": asset_id,
        "display_name": display_name,
        "physical_size_m": physical_size_m,
        "local_paths": local_paths,
        "resolution_used": resolution_used,
        "already_cached": False,
    }


def compute_uv_repeat(physical_size_m: float, model_unit_system: str) -> float:
    """Compute UV repeat factor so 1 UV unit = physical_size in model units.

    model_unit_system: "Millimeters", "Centimeters", "Meters", "Feet", "Inches", etc.
    Returns repeat factor (e.g., if model is in mm and tile is 1m, repeat = 1000)
    """
    unit_to_meters = {
        "Millimeters": 0.001,
        "Centimeters": 0.01,
        "Meters": 1.0,
        "Feet": 0.3048,
        "Inches": 0.0254,
    }

    unit_size_m = unit_to_meters.get(model_unit_system, 1.0)

    if physical_size_m <= 0:
        return 1.0

    return physical_size_m / unit_size_m
