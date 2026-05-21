r"""Externalize the icon + QR code from the Pencil-bundled docs/index.html.

The Pencil exporter base64-embeds every asset (fonts, images, scripts) into
a single HTML file. Fonts are small enough to keep inline, but the favicon
image weighs ~2.6 MB and bloats the HTML accordingly. This script:

  1. Replaces every reference to the bundled image UUIDs inside the template
     string with relative paths pointing at docs/assets/.
  2. Removes the corresponding entries from the manifest JSON so we don't
     keep ~3.5 MB of base64 around for nothing.

The canonical image files live in docs/assets/. They're the single source of
truth: GitHub Pages serves them, the README references them, and the bundle
points at them. The script hashes the files in docs/assets/ to identify
which bundle entries correspond to which canonical image.

The script is idempotent: re-running on an already-processed file is a no-op
because the UUIDs have already been substituted out. Re-run it after every
Pencil re-export to reapply the externalization.

Two non-obvious subtleties:
  - The template's content is JSON-encoded HTML, and that HTML contains
    inline <script>...</script> blocks. A non-greedy regex like (.*?)</script>
    matches the FIRST internal </script> instead of the script tag's real
    closing tag, truncating the content. We use the JSON-string anchor
    `"</script>` to find the real boundary instead.
  - When re-encoding the template with json.dumps(), literal `</` sequences
    in the output WILL terminate the surrounding <script> tag in the browser.
    We escape `</` to `<\/` to prevent that (JSON decoding still produces `</`,
    but the HTML parser sees `<\/script>` and ignores it).
"""
import base64
import hashlib
import json
import os
import sys


ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HTML = os.path.join(ROOT, 'docs', 'index.html')
DOCS_ASSETS = os.path.join(ROOT, 'docs', 'assets')


def slice_script_content(html, opening_tag, json_terminator='}'):
    """Return (start, end) of the content inside a Pencil bundler script tag.

    `opening_tag` is the full opening, e.g. `<script type="__bundler/manifest">`.
    `json_terminator` is the final character of the JSON value inside — `}` for
    objects (manifest, ext_resources) and `"` for strings (template). We anchor
    the closing boundary on `<json_terminator></script>` to skip past internal
    </script> sequences that appear inside the JSON string.
    """
    start = html.find(opening_tag)
    if start < 0:
        raise ValueError(f"Could not find opening tag: {opening_tag!r}")
    content_start = start + len(opening_tag)
    anchor = json_terminator + '</script>'
    anchor_pos = html.find(anchor, content_start)
    if anchor_pos < 0:
        raise ValueError(f"Could not find closing anchor {anchor!r} for {opening_tag!r}")
    # +1 keeps the json_terminator char in our slice; the rest belongs to </script>.
    return content_start, anchor_pos + 1


def find_uuid_for(manifest, predicate):
    for uuid, entry in manifest.items():
        if predicate(entry):
            return uuid
    return None


def main():
    with open(HTML, 'r', encoding='utf-8') as f:
        html = f.read()

    # Robust boundary detection — the non-greedy regex approach truncates
    # the template at its first internal </script>.
    m_start, m_end = slice_script_content(html, '<script type="__bundler/manifest">', '}')
    t_start, t_end = slice_script_content(html, '<script type="__bundler/template">', '"')

    manifest = json.loads(html[m_start:m_end])
    template = json.loads(html[t_start:t_end])

    # Hash docs/assets so we can identify which bundle entry corresponds to which.
    qr_path = os.path.join(DOCS_ASSETS, 'qrcode.webp')
    qr_hash = None
    if os.path.exists(qr_path):
        with open(qr_path, 'rb') as fh:
            qr_hash = hashlib.sha256(fh.read()).hexdigest()

    # Pencil embeds the icon as a single image/png used for <link rel="icon">.
    icon_uuid = find_uuid_for(manifest, lambda e: e.get('mime') == 'image/png')

    # The QR code is the only image/webp; verify by hash too.
    qr_uuid = None
    if qr_hash:
        for uuid, entry in manifest.items():
            if entry.get('mime') != 'image/webp':
                continue
            raw = base64.b64decode(entry.get('data', ''))
            if hashlib.sha256(raw).hexdigest() == qr_hash:
                qr_uuid = uuid
                break

    if icon_uuid:
        template = template.replace(icon_uuid, 'assets/icon.png')
        del manifest[icon_uuid]
        print("  externalized icon  -> docs/assets/icon.png")
    if qr_uuid:
        template = template.replace(qr_uuid, 'assets/qrcode.webp')
        del manifest[qr_uuid]
        print("  externalized QR    -> docs/assets/qrcode.webp")
    if not icon_uuid and not qr_uuid:
        print("  no bundled images to externalize; rewriting to repair </script> escaping")

    # Re-serialize, then escape </ as <\/ so internal closing-script-tag
    # sequences inside the JSON don't terminate our containing <script> tag.
    new_manifest = json.dumps(manifest, separators=(',', ':')).replace('</', '<\\/')
    new_template = json.dumps(template).replace('</', '<\\/')

    # Splice the new content into the file at the original slice positions.
    new_html = (
        html[:m_start] + new_manifest + html[m_end:t_start] + new_template + html[t_end:]
    )

    with open(HTML, 'w', encoding='utf-8') as f:
        f.write(new_html)

    print(f"\n  docs/index.html: {len(html)/1024:.0f} KB -> {len(new_html)/1024:.0f} KB")


if __name__ == '__main__':
    main()
