from flask import Flask, request, jsonify
import objaverse
import shutil, os
import re

app = Flask(__name__)

SERVE_DIR = os.path.join(os.path.dirname(__file__), "served_glbs")
os.makedirs(SERVE_DIR, exist_ok=True)

print("Loading LVIS annotations (first run downloads ~50MB)...")
lvis = objaverse.load_lvis_annotations()
print(f"Ready. {len(lvis)} LVIS categories loaded.")


@app.route('/health', methods=['GET'])
def health():
    return jsonify({
        "status": "ready",
        "categories": len(lvis)
    })


@app.route('/search', methods=['POST'])
def search():
    data     = request.json or {}
    tags     = [str(t).lower() for t in data.get('tags', []) if t is not None]
    category = str(data.get('category', '')).lower()

    # Multi-word prompts (e.g. "red emergency bag") need token-level matching.
    tokens = set()
    for raw in tags + [category]:
        tokens.update([w for w in re.split(r"[^a-z0-9]+", raw) if len(w) >= 2])

    # Score every LVIS category against incoming tags
    scored = []
    for lvis_cat, uids in lvis.items():
        cat_words = lvis_cat.lower().replace('_', ' ').split()
        score = 0
        for tag in tokens:
            if tag in cat_words:
                score += 10
            elif any(tag in w or w in tag for w in cat_words):
                score += 4
        if score > 0:
            scored.append((score, lvis_cat, uids))

    scored.sort(reverse=True)

    if not scored:
        return jsonify({ "found": False, "reason": f"no matching LVIS category for tokens={sorted(tokens)}" })

    # Pull top 3 UIDs across best categories
    candidate_uids = []
    for _, cat, uids in scored[:3]:
        candidate_uids.extend(uids[:2])
    candidate_uids = candidate_uids[:5]

    # Download GLBs (cached after first download in ~/.objaverse/)
    objects = objaverse.load_objects(uids=candidate_uids, download_processes=1)

    if not objects:
        return jsonify({ "found": False, "reason": "download failed" })

    uid      = list(objects.keys())[0]
    glb_path = objects[uid]

    # Copy into our served directory
    filename = f"{uid}.glb"
    dest     = os.path.join(SERVE_DIR, filename)
    shutil.copy(glb_path, dest)

    # Return local URL Unity will download from
    return jsonify({
        "found":   True,
        "uid":     uid,
        "glbUrl":  f"http://127.0.0.1:5001/glb/{filename}"
    })


@app.route('/glb/<filename>', methods=['GET'])
def serve_glb(filename):
    from flask import send_from_directory
    return send_from_directory(SERVE_DIR, filename)


if __name__ == '__main__':
    app.run(port=5001, threaded=True)