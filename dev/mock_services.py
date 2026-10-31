"""Mock Seerr, Radarr and Sonarr for exercising the plugin without real services.

Seerr on :5055 (key dev-seerr-key), Radarr on :7878 (dev-radarr-key), Sonarr on :8989 (dev-sonarr-key).
Downloads progress in real time and finish into "available", so live updates can be watched.

    python dev/mock_services.py --users <jellyfinId>:<name>,<jellyfinId>:<name>

Control endpoints (any port):
    POST /__mock/arr-remove?kind=movie&id=<tmdbId>   remove a title from Radarr (kind=tv uses tvdbId)
    POST /__mock/reset                               restore the sample data
"""

import argparse
import json
import threading
import time
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

KEYS = {5055: "dev-seerr-key", 7878: "dev-radarr-key", 8989: "dev-sonarr-key"}
LOCK = threading.Lock()
STATE = {}

TITLES = {
    ("movie", 550): ("Fight Club", "1999-10-15", "/pB8BM7pdSp6B6Ih7QZ4DrQ3PmJK.jpg"),
    ("movie", 603): ("The Matrix", "1999-03-30", "/f89U3ADr1oiB1s9GkdPOEpXUk5H.jpg"),
    ("movie", 157336): ("Interstellar", "2014-11-05", "/gEU2QniE6E77NI6lCU6MxlNBvIx.jpg"),
    ("movie", 27205): ("Inception", "2010-07-15", "/oYuLEt3zVCKq57qu2F8dT7NIa6f.jpg"),
    ("movie", 680): ("Pulp Fiction", "1994-09-10", "/d5iIlFn5s0ImszYzBPb8JPIfbXD.jpg"),
    ("movie", 13): ("Forrest Gump", "1994-06-23", "/arw2vcBveWOVZr6pxd9XTd1TdQa.jpg"),
    ("movie", 155): ("The Dark Knight", "2008-07-16", "/qJ2tW6WMUDux911r6m7haRef0WH.jpg"),
    ("tv", 1396): ("Breaking Bad", "2008-01-20", "/ggFHVNu6YYI5L9pCfOacjizRGt.jpg"),
    ("tv", 1399): ("Game of Thrones", "2011-04-17", "/1XS1oqL89opfnbLl8WnZY1O1uJx.jpg"),
}


def iso(dt):
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z")


def reset(users):
    now = datetime.now(timezone.utc)
    admin, sam = users[0], users[1] if len(users) > 1 else users[0]
    guest = {"id": 9, "displayName": "Guest (Seerr only)", "jellyfinUserId": None}

    def user(u, sid):
        return {"id": sid, "displayName": u["name"], "jellyfinUserId": u["id"], "jellyfinUsername": u["name"]}

    def req(rid, mid, type_, tmdb, tvdb, status, media_status, who, age_h, seasons=None, is4k=False):
        created = now - timedelta(hours=age_h)
        return {
            "id": rid, "status": status, "type": type_, "is4k": is4k,
            "createdAt": iso(created), "updatedAt": iso(created + timedelta(minutes=5)),
            "requestedBy": who,
            "seasons": [{"seasonNumber": s, "status": 2} for s in (seasons or [])],
            "media": {
                "id": mid, "mediaType": type_, "tmdbId": tmdb, "tvdbId": tvdb,
                "status": media_status if not is4k else 1, "status4k": media_status if is4k else 1,
                "downloadStatus": [],
            },
        }

    a, s = user(admin, 1), user(sam, 2)
    STATE["requests"] = [
        req(101, 1, "movie", 550, None, 2, 3, a, 3),
        req(102, 2, "movie", 603, None, 1, 2, s, 1),
        req(103, 3, "movie", 157336, None, 5, 5, s, 72),
        req(104, 4, "tv", 1396, 81189, 2, 4, a, 30, seasons=[1, 2, 3]),
        req(105, 5, "movie", 27205, None, 3, 1, guest, 48),
        req(106, 6, "tv", 1399, 121361, 2, 3, s, 6, seasons=[1]),
        req(107, 7, "movie", 680, None, 5, 7, a, 400),
        req(108, 8, "movie", 13, None, 5, 5, s, 200),
        req(109, 9, "movie", 155, None, 2, 3, a, 2, is4k=True),
    ]
    start = time.time()
    STATE["radarr_queue"] = [
        {"tmdbId": 550, "size": 8.0e9, "rate": 25e6, "start": start - 100},
    ]
    STATE["sonarr_queue"] = [
        {"tvdbId": 81189, "season": 3, "episode": e, "size": 1.6e9, "rate": 9e6, "start": start - 40 * e}
        for e in (1, 2, 3)
    ]
    STATE["radarr_movies"] = {550: False, 157336: True, 155: False, 13: False}
    STATE["sonarr_series"] = {81189: 12, 121361: 0}


def queue_records(kind):
    now = time.time()
    records, done = [], []
    for q in STATE[f"{kind}_queue"]:
        left = max(0.0, q["size"] - q["rate"] * (now - q["start"]))
        finished_for = (now - q["start"]) - q["size"] / q["rate"]
        if finished_for > 20:
            done.append(q)
            continue
        rec = {
            "size": q["size"], "sizeleft": left,
            "status": "downloading" if left > 0 else "completed",
            "trackedDownloadState": "downloading" if left > 0 else "importPending",
            "estimatedCompletionTime": iso(datetime.now(timezone.utc) + timedelta(seconds=left / q["rate"])),
        }
        if kind == "radarr":
            rec["movie"] = {"tmdbId": q["tmdbId"]}
        else:
            rec["series"] = {"tvdbId": q["tvdbId"]}
            rec["episode"] = {"seasonNumber": q["season"], "episodeNumber": q["episode"]}
        records.append(rec)

    # Finished downloads become available, like a real import + Seerr availability sync.
    for q in done:
        STATE[f"{kind}_queue"].remove(q)
        if kind == "radarr":
            STATE["radarr_movies"][q["tmdbId"]] = True
            for r in STATE["requests"]:
                if r["media"]["tmdbId"] == q["tmdbId"] and r["type"] == "movie":
                    r["media"]["status"] = 5
                    r["updatedAt"] = iso(datetime.now(timezone.utc))
        elif not any(x["tvdbId"] == q["tvdbId"] for x in STATE["sonarr_queue"]):
            for r in STATE["requests"]:
                if r["media"]["tvdbId"] == q["tvdbId"]:
                    r["media"]["status"] = 5
    return records


class Handler(BaseHTTPRequestHandler):
    server_version = "mock"

    def log_message(self, fmt, *args):
        print(f"[{self.server.server_port}] {self.command} {self.path} -> {args[1] if len(args) > 1 else ''}")

    def send(self, code, body=None):
        data = b"" if body is None else json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def route(self):
        url = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(url.query).items()}
        parts = [p for p in url.path.split("/") if p]
        port = self.server.server_port

        if parts[:1] == ["__mock"]:
            with LOCK:
                if parts[1:] == ["reset"]:
                    reset(self.server.users)
                elif parts[1:] == ["arr-remove"]:
                    if q.get("kind") == "tv":
                        STATE["sonarr_series"].pop(int(q["id"]), None)
                    else:
                        STATE["radarr_movies"].pop(int(q["id"]), None)
            return self.send(200, {"ok": True})

        if self.headers.get("X-Api-Key") != KEYS[port]:
            return self.send(401, {"message": "bad api key"})

        with LOCK:
            if port == 5055:
                return self.seerr(parts, q)
            return self.arr("radarr" if port == 7878 else "sonarr", parts)

    def seerr(self, parts, q):
        # Advance downloads so availability changes appear in Seerr too.
        queue_records("radarr")
        queue_records("sonarr")
        if parts == ["api", "v1", "request"] and self.command == "GET":
            take, skip = int(q.get("take", 20)), int(q.get("skip", 0))
            reqs = sorted(STATE["requests"], key=lambda r: r["createdAt"], reverse=True)
            return self.send(200, {"pageInfo": {"pages": 1, "pageSize": take, "results": len(reqs), "page": 1}, "results": reqs[skip:skip + take]})
        if len(parts) == 4 and parts[2] in ("movie", "tv") and self.command == "GET":
            t = TITLES.get((parts[2], int(parts[3])))
            if not t:
                return self.send(404, {"message": "not found"})
            key = "title" if parts[2] == "movie" else "name"
            date = "releaseDate" if parts[2] == "movie" else "firstAirDate"
            return self.send(200, {key: t[0], date: t[1], "posterPath": t[2]})
        if len(parts) == 5 and parts[2] == "media" and parts[4] == "file" and self.command == "DELETE":
            # Seerr removes the title (with files) from Radarr/Sonarr and marks the media deleted.
            for r in STATE["requests"]:
                if r["media"]["id"] == int(parts[3]):
                    if r["type"] == "movie":
                        STATE["radarr_movies"].pop(r["media"]["tmdbId"], None)
                        STATE["radarr_queue"] = [x for x in STATE["radarr_queue"] if x["tmdbId"] != r["media"]["tmdbId"]]
                    else:
                        STATE["sonarr_series"].pop(r["media"]["tvdbId"], None)
                        STATE["sonarr_queue"] = [x for x in STATE["sonarr_queue"] if x["tvdbId"] != r["media"]["tvdbId"]]
                    r["media"]["status4k" if q.get("is4k") == "true" else "status"] = 7
            print(f"[mock] removed media {parts[3]} from *arr with files (is4k={q.get('is4k')})")
            return self.send(204)
        if len(parts) == 4 and parts[2] == "media" and self.command == "DELETE":
            STATE["requests"] = [r for r in STATE["requests"] if r["media"]["id"] != int(parts[3])]
            return self.send(204)
        if len(parts) == 4 and parts[2] == "request" and self.command == "DELETE":
            STATE["requests"] = [r for r in STATE["requests"] if r["id"] != int(parts[3])]
            return self.send(204)
        if len(parts) == 5 and parts[2] == "request" and self.command == "POST":
            for r in STATE["requests"]:
                if r["id"] == int(parts[3]):
                    r["status"] = 2 if parts[4] == "approve" else 3
                    r["media"]["status"] = 3 if parts[4] == "approve" else 1
                    r["updatedAt"] = iso(datetime.now(timezone.utc))
                    if parts[4] == "approve" and r["type"] == "movie":
                        STATE["radarr_movies"][r["media"]["tmdbId"]] = False
                        STATE["radarr_queue"].append({"tmdbId": r["media"]["tmdbId"], "size": 4e9, "rate": 40e6, "start": time.time()})
                    return self.send(200, r)
            return self.send(404, {"message": "not found"})
        return self.send(404, {"message": "unknown route"})

    def arr(self, kind, parts):
        if parts == ["api", "v3", "system", "status"]:
            return self.send(200, {"appName": kind.capitalize(), "version": "mock"})
        if parts == ["api", "v3", "queue"]:
            return self.send(200, {"page": 1, "records": queue_records(kind)})
        if kind == "radarr" and parts == ["api", "v3", "movie"]:
            return self.send(200, [{"tmdbId": k, "hasFile": v} for k, v in STATE["radarr_movies"].items()])
        if kind == "sonarr" and parts == ["api", "v3", "series"]:
            return self.send(200, [{"tvdbId": k, "statistics": {"episodeFileCount": v}} for k, v in STATE["sonarr_series"].items()])
        return self.send(404, {"message": "unknown route"})

    do_GET = do_POST = do_DELETE = route


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--users", required=True, help="jellyfinId:name,jellyfinId:name (first is treated as the admin)")
    args = parser.parse_args()
    users = [{"id": u.split(":")[0], "name": u.split(":")[1]} for u in args.users.split(",")]
    reset(users)

    for port in KEYS:
        srv = ThreadingHTTPServer(("127.0.0.1", port), Handler)
        srv.users = users
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        print(f"mock listening on http://127.0.0.1:{port}")
    threading.Event().wait()


if __name__ == "__main__":
    main()
