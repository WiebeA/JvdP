Import("env")

import json
import os
from pathlib import Path

project_dir = Path(env["PROJECT_DIR"])
version = (project_dir / "VERSION").read_text(encoding="utf-8").strip()
ap_ssid = os.environ.get("JVDP_AP_SSID", "JvdP-LightSensor")
ap_password = os.environ.get("JVDP_AP_PASSWORD", "CHANGE-ME")
ota_password = os.environ.get("JVDP_OTA_PASSWORD", "CHANGE-ME-OTA")

if not version:
    raise RuntimeError("VERSION is empty")


def c_literal(value):
    return json.dumps(value, ensure_ascii=True)


generated_dir = project_dir / ".generated"
generated_dir.mkdir(exist_ok=True)
header_path = generated_dir / "JvdpBuildConfig.h"
header = """#pragma once
#define JVDP_VERSION {version}
#define JVDP_AP_SSID {ap_ssid}
#define JVDP_AP_PASSWORD {ap_password}
#define JVDP_OTA_PASSWORD {ota_password}
""".format(
    version=c_literal(version),
    ap_ssid=c_literal(ap_ssid),
    ap_password=c_literal(ap_password),
    ota_password=c_literal(ota_password),
)

if not header_path.exists() or header_path.read_text(encoding="utf-8") != header:
    header_path.write_text(header, encoding="utf-8", newline="\n")
