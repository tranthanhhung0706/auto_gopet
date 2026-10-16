"""Bộ đọc tối giản file SerializedFile của Unity (.assets), đủ để lấy TextAsset và MonoBehaviour thô.

Chỉ hỗ trợ format version >= 22 (Unity 2020+), đủ cho Unity 6000.x.
"""
import struct


class Reader:
    def __init__(self, data, pos=0, little=True):
        self.d, self.p, self.e = data, pos, '<' if little else '>'

    def u8(self):
        v = self.d[self.p]; self.p += 1; return v

    def _f(self, fmt, n):
        v = struct.unpack_from(self.e + fmt, self.d, self.p)[0]; self.p += n; return v

    def i16(self): return self._f('h', 2)
    def i32(self): return self._f('i', 4)
    def u32(self): return self._f('I', 4)
    def i64(self): return self._f('q', 8)

    def cstr(self):
        end = self.d.index(b'\0', self.p)
        s = self.d[self.p:end].decode('utf-8', 'replace'); self.p = end + 1; return s

    def align(self, n=4):
        self.p = (self.p + n - 1) // n * n

    def bytes_(self, n):
        v = self.d[self.p:self.p + n]; self.p += n; return v


class SerializedFile:
    def __init__(self, path):
        self.data = open(path, 'rb').read()
        r = Reader(self.data, 0, little=False)
        r.u32(); r.u32()
        self.version = r.u32()
        r.u32()
        if self.version < 22:
            raise ValueError(f'format version {self.version} chưa hỗ trợ')
        endian = r.u8(); r.p += 3
        r.u32()  # metadata size
        r.i64()  # file size
        self.data_offset = r.i64()
        r.i64()
        r = Reader(self.data, r.p, little=(endian == 0))
        self.unity_version = r.cstr()
        r.i32()  # platform
        self.type_tree = r.u8() != 0
        self.types = []
        for _ in range(r.i32()):
            self.types.append(self._read_type(r, False))
        self.objects = []
        n = r.i32()
        for _ in range(n):
            r.align(4)
            path_id = r.i64()
            start = r.i64()
            size = r.u32()
            tidx = r.i32()
            cls = self.types[tidx]['class_id']
            self.objects.append(dict(path_id=path_id, offset=self.data_offset + start, size=size, class_id=cls,
                                     type_index=tidx))
        self.r = r

    def _read_type(self, r, ref_type):
        t = dict(class_id=r.i32())
        r.u8()  # stripped
        t['script_index'] = r.i16()
        if (ref_type and t['script_index'] >= 0) or (not ref_type and (t['class_id'] == 114 or t['script_index'] >= 0)):
            r.bytes_(16)
        r.bytes_(16)
        if self.type_tree:
            nodes = r.i32(); sbuf = r.i32()
            r.p += nodes * 32 + sbuf
            if not ref_type:
                r.p += 4 * r.i32()
            else:
                r.cstr(); r.cstr(); r.cstr()
        return t

    def obj_reader(self, obj):
        return Reader(self.data, obj['offset'], little=True)

    def text_assets(self):
        """Trả về list (path_id, name, bytes) của mọi TextAsset (class 49)."""
        out = []
        for o in self.objects:
            if o['class_id'] != 49:
                continue
            r = self.obj_reader(o)
            name = r.bytes_(r.i32()).decode('utf-8', 'replace'); r.align()
            data = r.bytes_(r.i32())
            out.append((o['path_id'], name, data))
        return out
