import json
import subprocess
import sys
from pathlib import Path

SERVER = Path(r"E:\GitHub\renderdoc-mcp\_dist\package\renderdoc-mcp-windows-x64-v0.3.0\bin\renderdoc-mcp.exe")


class McpClient:
    def __init__(self):
        self.process = subprocess.Popen(
            [str(SERVER)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
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
                raise RuntimeError("renderdoc-mcp exited")
            response = json.loads(line)
            if response.get("id") == self.next_id:
                return response

    def notify(self, method, params=None):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def tool(self, name, **arguments):
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
        self.process.wait(timeout=20)


def main():
    if len(sys.argv) < 3:
        print("usage: rdmcp.py CAPTURE COMMAND [args]")
        return 2
    capture = sys.argv[1]
    command = sys.argv[2]
    args = sys.argv[3:]

    def options():
        values = {}
        index = 0
        while index < len(args):
            if args[index].startswith("--"):
                values[args[index][2:]] = args[index + 1]
                index += 2
            else:
                index += 1
        return values

    client = McpClient()
    try:
        client.request("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "ssl-p13-verification", "version": "1"},
        })
        client.notify("notifications/initialized")
        if command == "tools":
            print(json.dumps(client.request("tools/list"), indent=2))
            return 0
        opened = client.tool("open_capture", path=capture)
        values = options()
        event = int(values["event"]) if "event" in values else None
        if command == "list":
            result = client.tool(
                "list_cbuffers", stage=values.get("stage", "ps"),
                **({"eventId": event} if event is not None else {}))
            print(json.dumps(result, indent=2))
        elif command == "cbuffer":
            result = client.tool(
                "get_cbuffer_contents", stage=values.get("stage", "ps"),
                index=int(values["index"]),
                **({"eventId": event} if event is not None else {}))
            print(json.dumps(result, indent=2))
        elif command == "debug-pixel":
            result = client.tool(
                "debug_pixel", eventId=event,
                x=int(args[0]), y=int(args[1]), mode="trace")
            print(json.dumps(result, indent=2))
        elif command == "call":
            tool = args[0]
            arguments = json.loads(args[1]) if len(args) > 1 else {}
            print(json.dumps(client.tool(tool, **arguments), indent=2))
        else:
            print("unknown command")
            return 2
    finally:
        client.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
