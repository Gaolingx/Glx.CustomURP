"""Minimal stdio client for renderdoc-mcp.

The released renderdoc-mcp binary speaks newline-delimited JSON-RPC 2.0 on
stdin/stdout. This client opens a capture, then invokes one tool (or lists the
tool catalog) and prints the JSON result. It only uses the Python standard
library, so no virtual environment is required.

Examples:
    python MCP/tools/rdmcp.py tools
    python MCP/tools/rdmcp.py open path/to/capture.rdc
    python MCP/tools/rdmcp.py call path/to/capture.rdc get_capture_info
    python MCP/tools/rdmcp.py call path/to/capture.rdc list_draws '{"filter":"glass"}'
    python MCP/tools/rdmcp.py debug-pixel path/to/capture.rdc 640 359 --event 786 --out debug.json

The server executable can be overridden with the RENDERDOC_MCP_EXE environment
variable.
"""

import argparse
import json
import os
import subprocess
import sys

DEFAULT_SERVER = (
    r"E:\GitHub\renderdoc-mcp\_dist\package"
    r"\renderdoc-mcp-windows-x64-v0.3.0\bin\renderdoc-mcp.exe"
)


class McpClient:
    def __init__(self, server, verbose=False):
        self.process = subprocess.Popen(
            [str(server)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=None if verbose else subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )
        self.next_id = 0

    def request(self, method, params=None):
        self.next_id += 1
        message = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            message["params"] = params
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()
        while True:
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError("renderdoc-mcp exited before replying")
            response = json.loads(line)
            if response.get("id") == self.next_id:
                return response

    def notify(self, method, params=None):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def call_tool(self, name, **arguments):
        response = self.request("tools/call", {"name": name, "arguments": arguments})
        if "error" in response:
            raise RuntimeError(response["error"])
        result = response["result"]
        text = result["content"][0]["text"]
        if result.get("isError"):
            raise RuntimeError(text)
        return json.loads(text)

    def close(self):
        try:
            self.process.stdin.close()
        except OSError:
            pass
        try:
            self.process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()


def build_parser():
    parser = argparse.ArgumentParser(description="Call renderdoc-mcp over stdio.")
    parser.add_argument("--server", default=os.environ.get("RENDERDOC_MCP_EXE", DEFAULT_SERVER))
    parser.add_argument("--verbose", action="store_true", help="forward server stderr")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("tools", help="list MCP tools")

    open_parser = subparsers.add_parser("open", help="open a capture and print its info")
    open_parser.add_argument("capture")

    call_parser = subparsers.add_parser("call", help="call one MCP tool")
    call_parser.add_argument("capture")
    call_parser.add_argument("tool")
    call_parser.add_argument("arguments", nargs="?", default="{}",
                             help="JSON object with tool arguments")

    debug_parser = subparsers.add_parser("debug-pixel", help="trace a pixel shader")
    debug_parser.add_argument("capture")
    debug_parser.add_argument("x", type=int)
    debug_parser.add_argument("y", type=int)
    debug_parser.add_argument("--event", type=int, required=True, dest="event")
    debug_parser.add_argument("--primitive", type=int, default=None)
    debug_parser.add_argument("--out", help="write the JSON result to this file")
    return parser


def main():
    args = build_parser().parse_args()
    client = McpClient(args.server, args.verbose)
    try:
        client.request("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "rdmcp", "version": "1"},
        })
        client.notify("notifications/initialized")

        if args.command == "tools":
            print(json.dumps(client.request("tools/list"), indent=2))
            return 0

        info = client.call_tool("open_capture", path=args.capture)
        if args.command == "open":
            print(json.dumps(info, indent=2))
        elif args.command == "call":
            print(json.dumps(client.call_tool(args.tool, **json.loads(args.arguments)), indent=2))
        elif args.command == "debug-pixel":
            arguments = {"eventId": args.event, "x": args.x, "y": args.y, "mode": "trace"}
            if args.primitive is not None:
                arguments["primitive"] = args.primitive
            result = client.call_tool("debug_pixel", **arguments)
            text = json.dumps(result, indent=2)
            if args.out:
                with open(args.out, "w", encoding="utf-8") as output:
                    output.write(text)
                print("wrote " + args.out)
            else:
                print(text)
        return 0
    finally:
        client.close()


if __name__ == "__main__":
    sys.exit(main())
