"""Export the exact native-tool release and its submodule sources, without checkout."""
import io
import pathlib
import subprocess
import sys
import zipfile

REVISION = "54e40c426abcd38f93cd7f2bbafd9b1206696912"  # REL_2.19.0.0_EXTERNAL
root = pathlib.Path(__file__).resolve().parents[2]
output = pathlib.Path(sys.argv[1]).resolve()
if output.exists():
    raise SystemExit("Source archive already exists; use a new output path.")


def export(archive, repo, revision, prefix=""):
    data = subprocess.check_output(["git", "-C", str(repo), "archive", "--format=zip", revision])
    with zipfile.ZipFile(io.BytesIO(data)) as source:
        for entry in source.infolist():
            if not entry.is_dir():
                archive.writestr(prefix + entry.filename, source.read(entry))
    tree = subprocess.check_output(["git", "-C", str(repo), "ls-tree", "-r", revision], text=True)
    for line in tree.splitlines():
        if line.startswith("160000"):
            metadata, path = line.split("\t")
            export(archive, repo / path, metadata.split()[2], prefix + path + "/")


output.parent.mkdir(parents=True, exist_ok=True)
temporary = output.with_suffix(output.suffix + ".partial")
try:
    with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=5) as archive:
        export(archive, root, REVISION)
    temporary.replace(output)
except BaseException:
    temporary.unlink(missing_ok=True)
    raise
