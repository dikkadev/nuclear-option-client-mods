# /// script
# requires-python = ">=3.11"
# dependencies = ["websockets>=13"]
# ///
"""A stand-in NuclearLiveFeed for guide screenshots: says hello and describes a Heartland session, then stays open.
Units are injected into the page by the capture script; this only keeps the map's status at Live."""
import asyncio
import sys

import websockets

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 39555

HELLO = '<event version="2.0" type="b-x-nlf-hello"><detail><nlf protocol="1" control="targets"/></detail></event>'
TYPES = [
    (1, "aircraft", "FS-20", "FS-20 Vortex", 0),
    (2, "aircraft", "KR-67", "KR-67 Ifrit", 0),
    (3, "aircraft", "UH-90", "UH-90 Ibis", 0),
    (4, "ground", "Linebreaker", "Linebreaker SAM", 9000),
    (5, "ground", "Type-12", "Type-12 MBT", 0),
    (6, "munition", "AAM-29", "AAM-29 Scythe", 0),
    (7, "aircraft", "FS-12", "FS-12 Revoker", 0),
    (8, "ground", "StratoLance", "StratoLance R9 Launcher", 32000),
]
SESSION = (
    '<event version="2.0" type="b-x-nlf-session"><detail><nlf session="guide-demo" producer="guide">'
    '<map name="Heartland" width="81920" height="81920" latitude="45" longitude="0" altitude="0"/>'
    '<force id="1" name="Boscali" tag="BDF" color=""/><force id="2" name="Primeva" tag="PALA" color=""/>'
    + "".join(f'<type index="{i}" class="{c}" code="{code}" name="{name}" airRange="{r}"/>' for i, c, code, name, r in TYPES)
    + "</nlf></detail></event>"
)


async def serve(ws):
    await ws.send(HELLO)
    await ws.send(SESSION)
    await ws.wait_closed()


async def main():
    async with websockets.serve(serve, "127.0.0.1", PORT):
        print("feed on", PORT, flush=True)
        await asyncio.Future()


asyncio.run(main())
