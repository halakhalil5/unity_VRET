from flask import Flask, request, jsonify
import objaverse
import shutil, os
import uuid
import json
import urllib.request
import urllib.error

try:
    from shap_e.models.text_to_3d import sample_text_to_3d
    from shap_e.util.io import save_model
    SHAPE_E_AVAILABLE = True
    SHAPE_E_IMPORT_ERROR = ""
except Exception as e:
    SHAPE_E_AVAILABLE = False
    SHAPE_E_IMPORT_ERROR = str(e)

app = Flask(__name__)

SERVE_DIR = os.path.join(os.path.dirname(__file__), "served_glbs")
os.makedirs(SERVE_DIR, exist_ok=True)

REMOTE_SHAPE_E_URL = os.environ.get("REMOTE_SHAPE_E_URL", "").strip()
REMOTE_SHAPE_E_AUTH = os.environ.get("REMOTE_SHAPE_E_AUTH", "").strip()

print("Loading LVIS annotations (first run downloads ~50MB)...")
lvis = objaverse.load_lvis_annotations()
print(f"Ready. {len(lvis)} LVIS categories loaded.")


@app.route('/health', methods=['GET'])
def health():
    return jsonify({
        "status": "ready",
        "categories": len(lvis),
        "shapeERemoteConfigured": bool(REMOTE_SHAPE_E_URL),
        "shapeERemoteUrl": REMOTE_SHAPE_E_URL,
        "shapeEAvailable": SHAPE_E_AVAILABLE,
        "shapeEImportError": SHAPE_E_IMPORT_ERROR
    })


@app.route('/search', methods=['POST'])
def search():
    data     = request.json
    tags     = [t.lower() for t in data.get('tags', [])]
    category = data.get('category', '').lower()

    # Score every LVIS category against incoming tags
    scored = []
    for lvis_cat, uids in lvis.items():
        cat_words = lvis_cat.lower().replace('_', ' ').split()
        score = 0
        for tag in tags + [category]:
            if tag in cat_words:        score += 10
            elif any(tag in w for w in cat_words): score += 4
        if score > 0:
            scored.append((score, lvis_cat, uids))

    scored.sort(reverse=True)

    if not scored:
        return jsonify({ "found": False, "reason": "no matching LVIS category" })

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
        "glbUrl":  f"http://localhost:5001/glb/{filename}"
    })


@app.route('/glb/<filename>', methods=['GET'])
def serve_glb(filename):
    from flask import send_from_directory
    return send_from_directory(SERVE_DIR, filename)


@app.route('/generate_shap_e', methods=['POST'])
def generate_shap_e():
    data = request.json or {}
    prompt = (data.get('prompt') or '').strip()

    if not prompt:
        return jsonify({"found": False, "reason": "prompt is required"}), 400

    # If a remote Shape-E API is configured (e.g., Colab + ngrok), prefer it.
    if REMOTE_SHAPE_E_URL:
        remote_result = generate_shap_e_remote(prompt)
        if remote_result.get("found"):
            return jsonify(remote_result)

        # Fall through to local generation if remote fails but local is available.
        if not SHAPE_E_AVAILABLE:
            return jsonify(remote_result), 502

    if not SHAPE_E_AVAILABLE:
        return jsonify({
            "found": False,
            "reason": "shap_e import failed",
            "detail": SHAPE_E_IMPORT_ERROR
        }), 501

    try:
        model = sample_text_to_3d(prompt)
        filename = f"shapee_{uuid.uuid4().hex}.glb"
        output_path = os.path.join(SERVE_DIR, filename)
        save_model(model, output_path)

        return jsonify({
            "found": True,
            "glbUrl": f"http://localhost:5001/glb/{filename}",
            "source": "shape-e"
        })
    except Exception as e:
        return jsonify({"found": False, "reason": f"shape-e generation failed: {e}"}), 500


def generate_shap_e_remote(prompt):
    payload = json.dumps({"prompt": prompt}).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if REMOTE_SHAPE_E_AUTH:
        headers["Authorization"] = REMOTE_SHAPE_E_AUTH

    req = urllib.request.Request(
        REMOTE_SHAPE_E_URL,
        data=payload,
        method="POST",
        headers=headers,
    )

    try:
        with urllib.request.urlopen(req, timeout=180) as response:
            content_type = (response.headers.get("Content-Type") or "").lower()
            body = response.read()

            # Remote may return JSON with glbUrl or raw .glb bytes.
            if "application/json" in content_type:
                remote_json = json.loads(body.decode("utf-8"))
                glb_url = remote_json.get("glbUrl") or remote_json.get("url")
                if glb_url:
                    return {
                        "found": True,
                        "glbUrl": glb_url,
                        "source": "shape-e-remote-json"
                    }
                return {
                    "found": False,
                    "reason": f"remote JSON missing glbUrl/url: {remote_json}"
                }

            filename = f"shapee_remote_{uuid.uuid4().hex}.glb"
            out_path = os.path.join(SERVE_DIR, filename)
            with open(out_path, "wb") as f:
                f.write(body)

            return {
                "found": True,
                "glbUrl": f"http://localhost:5001/glb/{filename}",
                "source": "shape-e-remote-bytes"
            }
    except urllib.error.HTTPError as e:
        detail = ""
        try:
            detail = e.read().decode("utf-8")
        except Exception:
            detail = str(e)
        return {
            "found": False,
            "reason": f"remote Shape-E HTTP error {e.code}: {detail}"
        }
    except Exception as e:
        return {
            "found": False,
            "reason": f"remote Shape-E request failed: {e}"
        }


if __name__ == '__main__':
    app.run(port=5001, threaded=True)