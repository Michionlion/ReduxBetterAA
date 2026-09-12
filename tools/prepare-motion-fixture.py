"""Make a private, reproducible test INITIAL CONDITION; never alter original saves."""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

p=argparse.ArgumentParser();p.add_argument("source",type=Path);p.add_argument("destination",type=Path);p.add_argument("--scene-source",type=Path);a=p.parse_args()
if a.destination.exists(): raise SystemExit("Destination exists; refusing to overwrite an immutable fixture")
data=json.loads(a.source.read_text(encoding="utf-8-sig"))
v=next(v for v in data["Vessels"] if v["AssemblyDefinition"]["assemblyName"]=="Fly Safe-2")
r=v["location"]["rigidbodyState"]
normal=np.array([r["localPosition"][k] for k in "xyz"]); normal/=np.linalg.norm(normal)
if a.scene_source:
 scene=json.loads(a.scene_source.read_text(encoding="utf-8-sig"))
 sv=next(v for v in scene["Vessels"] if v["AssemblyDefinition"]["assemblyName"]=="Fly Safe-15")
 sr=sv["location"]["rigidbodyState"]
 normal=np.array([sr["localPosition"][k] for k in "xyz"]);normal/=np.linalg.norm(normal)
 r["localRotation"]=sr["localRotation"]
 data["Metadata"]["UniverseTime"]=scene["Metadata"]["UniverseTime"]
r["localPosition"]=dict(zip("xyz",(normal*608000).tolist()))
r["localVelocity"]=dict(zip("xyz",(-normal*280).tolist()))
r["localAngularVelocity"]={k:0.0 for k in "xyz"}
r["referenceFrameType"]="Body";r["PhysicsMode"]="RigidBody"
v["location"]["LocationType"]="SurfaceCoordinates"
v["vesselState"]["PhysicsMode"]="RigidBody";v["vesselState"]["Situation"]="Flying"
for k in v["vesselState"]["flightCtrlState"]: v["vesselState"]["flightCtrlState"][k]=None
a.destination.parent.mkdir(parents=True,exist_ok=True)
a.destination.write_text(json.dumps(data,indent=2))
manifest=dict(sourceSha256=hashlib.sha256(a.source.read_bytes()).hexdigest(),
    fixtureSha256=hashlib.sha256(a.destination.read_bytes()).hexdigest(),
    purpose="Test-only initial condition: original vessel/parts at 8 km over Kerbin, 280 m/s radial descent, zero angular velocity and neutral initial control inputs. Optional scene source supplies daylight time, launchpad radial location and upright rotation. Physics integrator, forces and timestep unchanged. Not an original recorded save.",
    sceneSourceSha256=hashlib.sha256(a.scene_source.read_bytes()).hexdigest() if a.scene_source else None)
a.destination.with_suffix(".manifest.json").write_text(json.dumps(manifest,indent=2))
print(json.dumps(manifest,indent=2))
