"""
privacy_check.py — scan a repository for things that should not be published.

    python tools/privacy_check.py            # scan the repo this file lives in
    python tools/privacy_check.py PATH       # scan another folder

Flags, in text files: IP addresses, personal paths (C:\\Users\\name, /Users/name, /home/name),
email addresses, API keys / tokens / passwords, machine names, USB device instance IDs and
MAC addresses. In images: EXIF (camera, time) and GPS location, comments and text chunks.
In videos: location / creation / device metadata (needs ffprobe on PATH).

Exits 1 if anything is found, so it can run before every push. A line that is safe on purpose
can end with the marker  privacy-ok  to be skipped.
"""
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

SKIP_DIRS = {".git", "out", "__pycache__", "node_modules", ".venv", "venv"}
TEXT_EXT = {".py", ".cs", ".md", ".txt", ".json", ".yml", ".yaml", ".toml", ".cfg", ".ini", ".env",
            ".sh", ".bat", ".ps1", ".shader", ".compute", ".hlsl", ".cginc", ".js", ".ts", ".html",
            ".css", ".cff", ".xml", ".asset", ".unity", ".prefab", ".meta", ".gitignore"}
IMAGE_EXT = {".jpg", ".jpeg", ".png", ".webp", ".heic", ".tif", ".tiff"}
VIDEO_EXT = {".mp4", ".mov", ".m4v", ".webm"}

OCTET = r"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)"
PATTERNS = {
    "IP address": re.compile(rf"(?<![\d.]){OCTET}\.{OCTET}\.{OCTET}\.{OCTET}(?![\d.])"),
    "personal path": re.compile(r"(?i)(?:[a-z]:[\\/]+users[\\/]+(?!public|default)[^\\/\s\"']+|/Users/(?!Shared)[^/\s\"']+|/home/[^/\s\"']+)"),
    "email": re.compile(r"[\w.+-]+@[\w-]+\.[\w.-]+"),
    "secret": re.compile(r"(?i)(?:api[_-]?key|secret|token|passw(?:or)?d)\s*[:=]\s*['\"]?[^\s'\"]{6,}|sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16}"),
    "machine name": re.compile(r"\b(?:DESKTOP|LAPTOP)-[A-Z0-9]{5,}\b"),
    "USB device ID": re.compile(r"(?i)USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\\S+"),
    "MAC address": re.compile(r"\b(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b", re.I),
}
SAFE_IPS = {"0.0.0.0", "127.0.0.1", "255.255.255.255"}
SAFE_EMAILS = re.compile(r"(?i)noreply@|@example\.(?:com|org)$|@users\.noreply\.github\.com$")


def scan_text(path, findings):
    try:
        text = path.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return
    for n, line in enumerate(text.splitlines(), 1):
        if "privacy-ok" in line:
            continue
        for label, rx in PATTERNS.items():
            for m in rx.finditer(line):
                hit = m.group(0)
                if label == "IP address" and hit in SAFE_IPS:
                    continue
                if label == "email" and SAFE_EMAILS.search(hit):
                    continue
                findings.append((path, f"line {n}: {label}: {hit[:60]}"))


def scan_image(path, findings):
    try:
        from PIL import Image
    except ImportError:
        findings.append((path, "image not checked (pip install pillow)"))
        return
    try:
        with Image.open(path) as im:
            exif = im.getexif()
            if exif:
                gps = exif.get_ifd(0x8825)
                findings.append((path, f"EXIF metadata ({len(exif)} tags){' including GPS location' if gps else ''}"))
            extra = {k for k in im.info if k not in {"dpi", "jfif", "jfif_version", "jfif_unit", "jfif_density",
                                                    "progressive", "progression", "loop", "duration", "background",
                                                    "timestamp", "transparency", "gamma", "aspect"}}
            if extra:
                findings.append((path, f"embedded metadata: {', '.join(sorted(extra))}"))
    except Exception as e:  # unreadable formats (e.g. HEIC without a plugin)
        findings.append((path, f"could not inspect image ({e.__class__.__name__}) — strip metadata before publishing"))


def scan_video(path, findings):
    if not shutil.which("ffprobe"):
        findings.append((path, "video not checked (install ffmpeg/ffprobe)"))
        return
    out = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "json", str(path)],
                         capture_output=True, text=True).stdout
    try:
        info = json.loads(out or "{}")
    except json.JSONDecodeError:
        return
    tags = dict(info.get("format", {}).get("tags", {}))
    for s in info.get("streams", []):
        tags.update(s.get("tags", {}))
    risky = {k: v for k, v in tags.items() if re.search(r"(?i)location|gps|creation_time|make|model|software|date", k)}
    if risky:
        findings.append((path, "video metadata: " + ", ".join(f"{k}={str(v)[:30]}" for k, v in risky.items())))


def main():
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent
    findings = []
    for path in sorted(root.rglob("*")):
        if not path.is_file() or any(part in SKIP_DIRS for part in path.relative_to(root).parts):
            continue
        ext = path.suffix.lower() if path.suffix else path.name.lower()
        if path.name == Path(__file__).name:
            continue                                   # this file's own patterns would match themselves
        if ext in TEXT_EXT or path.name in {"LICENSE", "README", ".gitignore"}:
            scan_text(path, findings)
        elif ext in IMAGE_EXT:
            scan_image(path, findings)
        elif ext in VIDEO_EXT:
            scan_video(path, findings)
    if not findings:
        print(f"privacy check: clean ({root})")
        return 0
    print(f"privacy check: {len(findings)} item(s) to review in {root}\n")
    for path, msg in findings:
        print(f"  {path.relative_to(root)}  —  {msg}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
