#!/usr/bin/env python3
"""RFB (VNC) サーバーのハンドシェイク前半を調べる調査用スクリプト。

Mac 上で実行する想定（認証は行わない。資格情報も一切扱わない）。

    python3 probe_rfb.py              # バージョンとセキュリティタイプ一覧
    python3 probe_rfb.py --ard        # さらにタイプ 30 を選び、DH パラメータを表示して切断

Windows から実行する場合（リポジトリを Mac にコピーせずに）:

    ssh mac python3 - --ard < tools/probe_rfb.py
"""

import argparse
import socket
import struct
import sys

KNOWN_TYPES = {
    0: "Invalid",
    1: "None",
    2: "VNC Authentication",
    5: "RA2",
    6: "RA2ne",
    16: "Tight",
    18: "TLS",
    19: "VeNCrypt",
    30: "Apple Remote Desktop (DH + AES)",
}


def recv_exact(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise EOFError("connection closed after %d of %d bytes" % (len(buf), n))
        buf += chunk
    return buf


def read_reason(sock):
    (length,) = struct.unpack(">I", recv_exact(sock, 4))
    return recv_exact(sock, length).decode("utf-8", "replace")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=5900)
    ap.add_argument("--timeout", type=float, default=5.0)
    ap.add_argument("--ard", action="store_true", help="select security type 30 and dump DH parameters")
    args = ap.parse_args()

    with socket.create_connection((args.host, args.port), timeout=args.timeout) as sock:
        version = recv_exact(sock, 12)
        print("server version : %r" % version)
        sock.sendall(b"RFB 003.008\n")

        (count,) = struct.unpack(">B", recv_exact(sock, 1))
        if count == 0:
            print("server refused : %s" % read_reason(sock))
            return 1

        types = list(recv_exact(sock, count))
        print("security types : %s" % types)
        for t in types:
            print("  %3d  %s" % (t, KNOWN_TYPES.get(t, "unknown / Apple-specific")))

        if not args.ard:
            return 0
        if 30 not in types:
            print("type 30 is not offered")
            return 1

        sock.sendall(bytes([30]))
        generator, key_length = struct.unpack(">HH", recv_exact(sock, 4))
        prime = recv_exact(sock, key_length)
        server_pub = recv_exact(sock, key_length)
        print("ARD generator  : %d" % generator)
        print("ARD keyLength  : %d bytes (%d bits)" % (key_length, key_length * 8))
        print("ARD prime      : %s..%s" % (prime[:8].hex(), prime[-8:].hex()))
        print("ARD server pub : %s..%s" % (server_pub[:8].hex(), server_pub[-8:].hex()))
        # ここで切断する（資格情報は送らない）
    return 0


if __name__ == "__main__":
    sys.exit(main())
