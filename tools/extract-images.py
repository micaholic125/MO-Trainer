# 一次性开发脚本：把会话记录（.jsonl）里粘贴过的图片抽出来存成 PNG，好做离线分析。
# 只读会话文件，只写 %TEMP%\mo-shroudscan\ 下的 png。不打印 base64。
import base64, io, json, os, sys

path = sys.argv[1]
outdir = sys.argv[2]
os.makedirs(outdir, exist_ok=True)

EXT = {"image/png": ".png", "image/jpeg": ".jpg", "image/gif": ".gif", "image/webp": ".webp"}

found = []

def walk(node, where):
    if isinstance(node, dict):
        if node.get("type") == "image":
            src = node.get("source") or {}
            if src.get("type") == "base64" and src.get("data"):
                found.append((src.get("media_type") or "image/png", src["data"], where))
        for k, v in node.items():
            walk(v, where if k not in ("content",) else where)
    elif isinstance(node, list):
        for v in node:
            walk(v, where)

with io.open(path, encoding="utf-8", errors="replace") as fh:
    for lineno, line in enumerate(fh, 1):
        line = line.strip()
        if not line or '"image"' not in line:
            continue
        try:
            obj = json.loads(line)
        except Exception as exc:
            print("line %d: json error %s" % (lineno, exc))
            continue
        before = len(found)
        walk(obj, "line%d" % lineno)
        for i in range(before, len(found)):
            mt, data, where = found[i]
            raw = base64.b64decode(data)
            ext = EXT.get(mt, ".bin")
            name = "img-%03d%s" % (i + 1, ext)
            dst = os.path.join(outdir, name)
            with open(dst, "wb") as out:
                out.write(raw)
            # 顺手把 PNG 的宽高读出来（IHDR 就在开头 24 字节）
            dims = ""
            if raw[:8] == b"\x89PNG\r\n\x1a\n":
                w = int.from_bytes(raw[16:20], "big")
                h = int.from_bytes(raw[20:24], "big")
                dims = " %dx%d px" % (w, h)
            print("%s  (%s, %d bytes)%s  [%s]" % (name, mt, len(raw), dims, where))

print("共抽出 %d 张图 -> %s" % (len(found), outdir))
