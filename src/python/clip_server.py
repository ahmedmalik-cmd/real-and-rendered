"""
CLIP embedding server — runs on the strong PC alongside tribe_server.py.

Gives the installation a real visual-similarity signal so the room can
recognise "have you been here before". The Kinect client POSTs a visit
frame here and stores the returned embedding; later visits are matched by
cosine similarity against stored embeddings.

Standalone on purpose — does NOT modify tribe_server.py.

── Install (once, on the 3090) ───────────────────────────────────────────────
    pip install open_clip_torch
  (fastapi / uvicorn / python-multipart were already installed for tribe_server.
   torch is already present from the TRIBE setup. pillow comes with torchvision.)

── Run ───────────────────────────────────────────────────────────────────────
    python clip_server.py
  Serves on http://0.0.0.0:8001 :
    GET  /health  → {"status": "ok", ...}
    POST /embed   → multipart 'image' field → {"embedding": [512 floats]}

Add it to the 3090's startup folder for the week-long run, same as tribe_server.
"""
import io
import sys

try:
    import torch
    import open_clip
    from PIL import Image
    from fastapi import FastAPI, UploadFile, File
    import uvicorn
except Exception as e:
    print(f"[CLIP] missing dependency: {e}")
    print("[CLIP] run:  pip install open_clip_torch")
    sys.exit(1)

DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
MODEL_NAME, PRETRAINED = "ViT-B-32", "laion2b_s34b_b79k"

print(f"[CLIP] loading {MODEL_NAME} ({PRETRAINED}) on {DEVICE} ...")
_model, _, _preprocess = open_clip.create_model_and_transforms(MODEL_NAME, pretrained=PRETRAINED)
_model = _model.to(DEVICE).eval()
print(f"[CLIP] model ready — embedding dim {_model.visual.output_dim}")

app = FastAPI()

@app.get("/health")
def health():
    return {"status": "ok", "model": MODEL_NAME, "device": DEVICE,
            "dim": int(_model.visual.output_dim)}

@app.post("/embed")
async def embed(image: UploadFile = File(...)):
    try:
        data = await image.read()
        img  = Image.open(io.BytesIO(data)).convert("RGB")
        t    = _preprocess(img).unsqueeze(0).to(DEVICE)
        with torch.no_grad():
            feat = _model.encode_image(t)
            feat = feat / feat.norm(dim=-1, keepdim=True)   # unit length
        return {"embedding": feat[0].cpu().tolist()}
    except Exception as e:
        return {"error": str(e)}

if __name__ == "__main__":
    # host 0.0.0.0 so the installation PC can reach it over the LAN.
    uvicorn.run(app, host="0.0.0.0", port=8001)
