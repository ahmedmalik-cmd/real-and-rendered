"""
TRIBE v2 server: runs on the GPU PC and predicts cortical activation for each clip.

The installation POSTs a 2-second clip here. TRIBE v2 predicts the brain's response on the
fsaverage5 cortical mesh (~20k vertices) for each timestep; the reply is the mean activation
per timestep plus the ten most active vertices.

    python tribe_server.py
    GET  /health   -> {"status": "ok"}
    POST /predict  -> multipart 'video' field -> {"activation": [...], "peak_vertices": [...], "shape": [...]}

TRIBE_CACHE sets where the model and its features are stored (default ./tribe_cache). Features
are cached per clip, so that folder keeps growing during a run; clear it between runs.
"""
# The checkpoint's config was written on Linux and holds PosixPath objects, which can't be
# created on Windows. Rebuild them as whatever Path is native here, before tribev2 reads it.
import pathlib, yaml
yaml.add_constructor(
    'tag:yaml.org,2002:python/object/apply:pathlib.PosixPath',
    lambda loader, node: pathlib.Path(*loader.construct_sequence(node)),
    Loader=yaml.UnsafeLoader
)

from fastapi import FastAPI, UploadFile, File
from fastapi.responses import JSONResponse
from tribev2 import TribeModel
import numpy as np
import tempfile, os, uvicorn

CACHE_FOLDER = os.environ.get("TRIBE_CACHE", "tribe_cache")

app = FastAPI()

print("Loading TRIBE v2 model...")
model = TribeModel.from_pretrained("facebook/tribev2", cache_folder=CACHE_FOLDER)
print("Model ready.")

# Deliberately blocks the event loop: one clip at a time on the GPU.
@app.post("/predict")
async def predict(video: UploadFile = File(...)):
    with tempfile.NamedTemporaryFile(suffix=".mp4", delete=False) as tmp:
        tmp.write(await video.read())
        tmp_path = tmp.name
    try:
        df = model.get_events_dataframe(video_path=tmp_path)
        preds, segments = model.predict(events=df)
        # preds shape: (n_timesteps, ~20k vertices)
        # Return mean activation per timestep + top activated regions
        mean_activation = preds.mean(axis=1).tolist()
        peak_vertices = np.argsort(preds.mean(axis=0))[-10:].tolist()
        return JSONResponse({
            "activation": mean_activation,        # list of floats, one per timestep
            "peak_vertices": peak_vertices,        # top 10 most active vertices
            "shape": list(preds.shape)
        })
    except Exception as e:
        return JSONResponse({"error": str(e)}, status_code=500)
    finally:
        os.unlink(tmp_path)

@app.get("/health")
async def health():
    return {"status": "ok"}

if __name__ == "__main__":
    # host 0.0.0.0 so the installation PC can reach it over the LAN.
    uvicorn.run(app, host="0.0.0.0", port=8000)
