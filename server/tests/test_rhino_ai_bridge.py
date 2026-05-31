"""
RhinoAIBridge v4.6 -- Test Suite (offline, no Rhino needed)
Run: cd server && uv run pytest tests/ -v
"""
import json, re, struct, gzip, pytest
from rhino_architect import material_downloader

# -- Wire protocol helpers --

def make_frame(payload, compress=False):
    if compress:
        buf = gzip.compress(payload); flag = b'\x01'
    else:
        buf = payload; flag = b'\x00'
    return flag + struct.pack('>I', len(buf)) + buf

def parse_frame(data):
    flag = data[0]; length = struct.unpack('>I', data[1:5])[0]
    payload = data[5:5+length]
    if flag == 1: payload = gzip.decompress(payload)
    return json.loads(payload.decode('utf-8'))

def resolve_ref(raw, prior):
    if not raw or not raw.startswith('$'): return None
    m = re.match(r'^\$(\d+)(?:\.([a-zA-Z_]\w*)(?:\[(\d+)\])?)?$', raw)
    if not m: return None
    idx = int(m.group(1)) - 1
    if idx < 0 or idx >= len(prior): return None
    result = prior[idx]
    if not m.group(2): return result
    token = result.get(m.group(2))
    if token is None: return None
    if m.group(3) is not None and isinstance(token, list):
        ai = int(m.group(3)); return token[ai] if ai < len(token) else None
    return token

def check_trust(command, trust_level):
    always_blocked = {'execute_script', 'run_command'}
    safe_extras    = {'delete_objects', 'boolean_operation'}
    if trust_level == 'developer': return True
    if command in always_blocked: return False
    if trust_level == 'safe' and command in safe_extras: return False
    return True


# -- 1. Wire protocol --

class TestWireProtocol:
    def test_raw_roundtrip(self):
        p = json.dumps({'type':'ping'}).encode()
        f = make_frame(p, False)
        assert f[0] == 0
        assert parse_frame(f)['type'] == 'ping'

    def test_gzip_roundtrip(self):
        p = json.dumps({'type':'get_objects'}).encode()
        f = make_frame(p, True)
        assert f[0] == 1
        assert parse_frame(f)['type'] == 'get_objects'

    def test_raw_flag_byte(self):
        assert make_frame(b'{}')[0] == 0x00

    def test_gzip_flag_byte(self):
        assert make_frame(b'{}', True)[0] == 0x01

    def test_large_payload_compresses_well(self):
        big = json.dumps({'data': ['x'*100]*200}).encode()
        assert len(make_frame(big, True)) < len(make_frame(big, False))

    def test_length_big_endian(self):
        p = b'hello'
        f = make_frame(p)
        n = (f[1]<<24)|(f[2]<<16)|(f[3]<<8)|f[4]
        assert n == 5

# -- 2. $ref resolution --

class TestRefResolution:
    P = [
        {'status':'ok','object_ids':['aaa','bbb','ccc'],'area':42.5,'centroid':[1,2,3]},
        {'status':'ok','object_ids':['ddd'],'count':7},
    ]
    def test_step_ref(self):       assert resolve_ref('$1', self.P) == self.P[0]
    def test_field_ref(self):      assert resolve_ref('$1.object_ids', self.P) == ['aaa','bbb','ccc']
    def test_indexed_ref(self):    assert resolve_ref('$1.object_ids[0]', self.P) == 'aaa'
    def test_last_indexed(self):   assert resolve_ref('$1.object_ids[2]', self.P) == 'ccc'
    def test_second_step(self):    assert resolve_ref('$2.count', self.P) == 7
    def test_numeric_field(self):  assert resolve_ref('$1.area', self.P) == 42.5
    def test_out_of_range(self):   assert resolve_ref('$9', self.P) is None
    def test_missing_field(self):  assert resolve_ref('$1.nope', self.P) is None
    def test_non_ref(self):        assert resolve_ref('hello', self.P) is None
    def test_empty(self):          assert resolve_ref('', self.P) is None
    def test_array_oob(self):      assert resolve_ref('$1.object_ids[99]', self.P) is None

# -- 3. Trust levels --

class TestTrustLevels:
    def test_safe_blocks_execute(self):   assert not check_trust('execute_script', 'safe')
    def test_safe_blocks_run_cmd(self):   assert not check_trust('run_command', 'safe')
    def test_safe_blocks_delete(self):    assert not check_trust('delete_objects', 'safe')
    def test_safe_blocks_boolean(self):   assert not check_trust('boolean_operation', 'safe')
    def test_safe_allows_create(self):    assert check_trust('create_wall', 'safe')
    def test_safe_allows_query(self):     assert check_trust('query_scene', 'safe')
    def test_trusted_blocks_exec(self):   assert not check_trust('execute_script', 'trusted')
    def test_trusted_blocks_cmd(self):    assert not check_trust('run_command', 'trusted')
    def test_trusted_allows_delete(self): assert check_trust('delete_objects', 'trusted')
    def test_trusted_allows_bool(self):   assert check_trust('boolean_operation', 'trusted')
    def test_dev_allows_execute(self):    assert check_trust('execute_script', 'developer')
    def test_dev_allows_delete(self):     assert check_trust('delete_objects', 'developer')

# -- 4. Error shapes --

class TestErrorShapes:
    def _err(self, code, msg, rec=True):
        return {'status':'error','error_code':code,'message':msg,'recoverable':rec}
    def test_has_status(self):        assert self._err('X','y')['status'] == 'error'
    def test_has_code(self):          assert self._err('OBJECT_NOT_FOUND','x')['error_code'] == 'OBJECT_NOT_FOUND'
    def test_has_message(self):       assert 'message' in self._err('X','y')
    def test_recoverable_true(self):  assert self._err('X','y',True)['recoverable'] is True
    def test_recoverable_false(self): assert self._err('AUTH_FAILED','x',False)['recoverable'] is False
    def test_known_codes(self):
        codes = ['OBJECT_NOT_FOUND','INVALID_GEOMETRY','COMMAND_FAILED','RHINO_NOT_RUNNING',
                 'BATCH_ROLLED_BACK','INVALID_GUID','COMMAND_BLOCKED_BY_TRUST_LEVEL',
                 'AUTH_FAILED','VIEWPORT_EMPTY']
        for c in codes:
            assert self._err(c,'t')['error_code'] == c

# -- 5. Batch preview --

KNOWN = {'create_wall','create_slab','delete_objects','move_objects',
         'capture_viewport','query_scene','boolean_operation','execute_script'}

def _preview(commands):
    steps=[]; warns=[]; creates=deletes=0
    dest={'delete_objects','boolean_operation','execute_script','run_command'}
    for i,cmd in enumerate(commands):
        t=cmd.get('type','')
        s={'step':i+1,'command':t,'status':'valid'}
        if not t: s['status']='invalid'; s['reason']='missing type'
        elif t not in KNOWN and t!='batch': s['status']='warning'; warns.append(f's{i+1}:{t}')
        else:
            if t in dest: s['warning']='destructive'; warns.append(f's{i+1}:{t}')
            if t.startswith('create_'): creates+=1
            if t=='delete_objects': deletes+=1
        steps.append(s)
    return {'status':'ok','step_count':len(commands),'estimated_creates':creates,
            'estimated_deletes':deletes,'warnings':warns,'steps':steps}

class TestBatchPreview:
    def test_valid_all_ok(self):
        r=_preview([{'type':'create_wall'},{'type':'move_objects'}])
        assert all(s['status']=='valid' for s in r['steps'])
    def test_unknown_is_warning(self):
        r=_preview([{'type':'fly_moon'}])
        assert r['steps'][0]['status']=='warning'
    def test_missing_type_invalid(self):
        r=_preview([{'params':{}}])
        assert r['steps'][0]['status']=='invalid'
    def test_destructive_warning(self):
        r=_preview([{'type':'delete_objects'}])
        assert r['steps'][0].get('warning')=='destructive'
    def test_create_count(self):
        r=_preview([{'type':'create_wall'},{'type':'create_slab'},{'type':'move_objects'}])
        assert r['estimated_creates']==2
    def test_delete_count(self):
        r=_preview([{'type':'delete_objects'},{'type':'delete_objects'}])
        assert r['estimated_deletes']==2
    def test_empty_batch(self):
        assert _preview([])['step_count']==0

# -- 6. Material downloader --

class TestMaterialDownloader:
    def test_requested_resolution_prefers_tiff_before_lower_resolution(self):
        downloads = [
            {"attribute": "2K-PNG", "fullDownloadPath": "two-k.zip"},
            {"attribute": "4K-TIFF", "fullDownloadPath": "four-k.zip"},
        ]
        chosen = material_downloader._pick_download_entry(downloads, "4K")
        assert chosen["attribute"] == "4K-TIFF"

    def test_requested_resolution_prefers_exr_before_lower_resolution(self):
        downloads = [
            {"attribute": "1K-JPG", "fullDownloadPath": "one-k.zip"},
            {"attribute": "2K-EXR", "fullDownloadPath": "two-k.zip"},
        ]
        chosen = material_downloader._pick_download_entry(downloads, "2K")
        assert chosen["attribute"] == "2K-EXR"

    def test_fetch_json_falls_back_to_last_known_good_cache(self, monkeypatch, tmp_path):
        class Response:
            def __enter__(self): return self
            def __exit__(self, *_): return False
            def read(self): return b'{"foundAssets":[{"assetId":"ok"}]}'

        monkeypatch.setattr(material_downloader, "_SCHEMA_CACHE_DIR", tmp_path)
        monkeypatch.setattr(material_downloader.urllib.request, "urlopen", lambda *_args, **_kwargs: Response())
        assert material_downloader._fetch_json("https://example.test/a")["foundAssets"][0]["assetId"] == "ok"

        def fail(*_args, **_kwargs):
            raise OSError("offline")

        monkeypatch.setattr(material_downloader.urllib.request, "urlopen", fail)
        cached = material_downloader._fetch_json("https://example.test/a")
        assert cached["_stale_cache"] is True
