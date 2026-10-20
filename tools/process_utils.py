"""Console-free native CLI children, with diagnostics retained in the caller's log."""
import subprocess
import sys


def background_kwargs() -> dict:
    # CREATE_NO_WINDOW prevents console allocation, including brief flashing windows.
    return {'creationflags': 0x08000000} if sys.platform == 'win32' else {}


def run_logged(args, *, cwd=None, env=None) -> None:
    completed = subprocess.run(args, cwd=cwd, env=env, check=False,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, encoding='utf-8', errors='replace',
                               **background_kwargs())
    if isinstance(completed.stdout, str) and completed.stdout:
        print(completed.stdout, end='', flush=True)
    completed.check_returncode()
