import struct
import os
import sys

# Locate the repository root by walking up from this script until we find a
# directory that contains a 'Resourcefile' folder. This keeps the script
# location-independent (works from any checkout or CI runner).
def find_repo_root(start):
    cur = os.path.abspath(start)
    while True:
        if os.path.isdir(os.path.join(cur, 'Resourcefile')):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            return None
        cur = parent

REPO_ROOT = find_repo_root(os.path.dirname(os.path.abspath(__file__)))
if REPO_ROOT is None:
    sys.exit('Could not locate repository root (no Resourcefile directory found).')
LANG_DIR = os.path.join(REPO_ROOT, 'Resourcefile', 'lang')


def unquote(s):
    s = s.strip()
    if len(s) >= 2 and s[0] == '"' and s[-1] == '"':
        return s[1:-1]
    return s


def parse_po(path):
    entries = []
    cur_id = None
    cur_str = None
    state = None
    with open(path, encoding='utf-8') as f:
        for line in f:
            line = line.rstrip('\n')
            if not line or line.startswith('#'):
                continue
            if line.startswith('msgid '):
                if cur_id is not None:
                    entries.append((cur_id, cur_str if cur_str is not None else ''))
                cur_id = unquote(line[6:])
                cur_str = None
                state = 'id'
            elif line.startswith('msgstr '):
                cur_str = unquote(line[7:])
                state = 'str'
            elif line.startswith('"'):
                chunk = unquote(line)
                if state == 'id':
                    cur_id += chunk
                else:
                    cur_str += chunk
    if cur_id is not None:
        entries.append((cur_id, cur_str if cur_str is not None else ''))
    entries = [(m, s) for (m, s) in entries if m != '']
    return entries


def write_mo(path, entries):
    pairs = sorted(entries, key=lambda x: x[0])
    keys = [p[0].encode('utf-8') for p in pairs]
    vals = [p[1].encode('utf-8') for p in pairs]
    n = len(keys)
    header = 28
    orig_off = header
    trans_off = header + n * 8
    str_off = trans_off + n * 8
    orig_pos = []
    trans_pos = []
    pos = str_off
    for k in keys:
        orig_pos.append(pos)
        pos += len(k) + 1
    for v in vals:
        trans_pos.append(pos)
        pos += len(v) + 1
    out = bytearray()
    out += struct.pack('<I', 0x950412de)
    out += struct.pack('<I', 0)
    out += struct.pack('<I', n)
    out += struct.pack('<I', orig_off)
    out += struct.pack('<I', trans_off)
    out += struct.pack('<I', 0)
    out += struct.pack('<I', 0)
    for i in range(n):
        out += struct.pack('<I', len(keys[i]))
        out += struct.pack('<I', orig_pos[i])
    for i in range(n):
        out += struct.pack('<I', len(vals[i]))
        out += struct.pack('<I', trans_pos[i])
    for k in keys:
        out += k + b'\x00'
    for v in vals:
        out += v + b'\x00'
    with open(path, 'wb') as f:
        f.write(out)


def esc(s):
    return s.replace('\\', '\\\\').replace('"', '\\"')


def write_po(path, entries, translate):
    with open(path, 'w', encoding='utf-8') as f:
        f.write('# Auto-generated from main.zh.po\nmsgid ""\nmsgstr ""\n')
        f.write('"Content-Type: text/plain; charset=UTF-8\\n"\n\n')
        for m, s in entries:
            f.write('#: ModEntry.cs\n')
            f.write('msgid "%s"\n' % esc(m))
            if translate:
                f.write('msgstr "%s"\n\n' % esc(s))
            else:
                f.write('msgstr ""\n\n')


def main():
    os.makedirs(LANG_DIR, exist_ok=True)
    zh = parse_po(os.path.join(LANG_DIR, 'main.zh.po'))
    if not zh:
        raise SystemExit('no entries parsed from main.zh.po')
    write_mo(os.path.join(LANG_DIR, 'main.zh.mo'), zh)
    write_po(os.path.join(LANG_DIR, 'main.en.po'), zh, translate=True)
    write_po(os.path.join(LANG_DIR, 'main.en.pot'), zh, translate=False)
    # English fallback .mo: msgstr == msgid so GetText has a base catalog.
    en_entries = [(m, m) for (m, s) in zh]
    write_mo(os.path.join(LANG_DIR, 'main.en.mo'), en_entries)
    print('generated %d entries -> main.zh.mo, main.en.mo, main.en.po, main.en.pot' % len(zh))


if __name__ == '__main__':
    main()
