from __future__ import annotations

import contextlib
import os
import posixpath
import tempfile
from ftplib import FTP, all_errors
from pathlib import Path, PurePosixPath


class HotspotTransferError(RuntimeError):
    pass


def validate_hotspot_template(folder):
    folder = Path(folder)
    if not folder.is_dir():
        raise FileNotFoundError(folder)
    files = {path.name.lower() for path in folder.rglob("*") if path.is_file()}
    if not (folder / "login.html").is_file():
        raise ValueError("القالب لا يحتوي على ملف login.html المطلوب للـ Hotspot.")
    recommended = {"alogin.html", "status.html", "logout.html"}
    return {
        "valid": True,
        "files": len(files),
        "missing_recommended": sorted(recommended - files),
    }


def _connect(host, username, password, port, timeout=15):
    ftp = FTP()
    ftp.encoding = "utf-8"
    try:
        ftp.connect(str(host), int(port), timeout=float(timeout))
        ftp.login(str(username), str(password or ""))
        ftp.set_pasv(True)
        return ftp
    except all_errors as exc:
        # The socket must never leak, but a failure while closing the broken
        # connection must not replace the real login/connect error that the
        # operator needs to see.
        with contextlib.suppress(all_errors, OSError):
            ftp.close()
        raise HotspotTransferError("فشل اتصال FTP: %s" % exc) from exc


def _close(ftp):
    try:
        ftp.quit()
    except all_errors:
        with contextlib.suppress(all_errors, OSError):
            ftp.close()


def _remote_parts(path):
    result = []
    for part in PurePosixPath(str(path).replace("\\", "/")).parts:
        if part in ("", "/", "."):
            continue
        if (part == ".." or any(ord(c) < 32 or ord(c) == 127 for c in part)
                or any(c in part for c in ':*?"<>|') or part.endswith((' ', '.'))
                or part.split('.')[0].upper() in {'CON','PRN','AUX','NUL',
                    *('COM%d' % i for i in range(1,10)), *('LPT%d' % i for i in range(1,10))}):
            raise HotspotTransferError("مسار FTP غير آمن: %s" % path)
        result.append(part)
    return result


def _remote_directory_parts(path):
    """Return a non-root RouterOS FTP directory.

    Uploading a Hotspot template to ``/`` or ``.`` can overwrite unrelated
    router files.  The UI normally proposes ``hotspot`` but the core transfer
    layer must enforce this invariant as well.
    """
    parts = _remote_parts(path)
    if not parts:
        raise HotspotTransferError("رفض استخدام جذر ملفات الراوتر؛ حدد مجلد Hotspot صريحًا.")
    return parts


def _ensure_remote_dir(ftp, path):
    parts = _remote_parts(path)
    ftp.cwd("/")
    for part in parts:
        try:
            ftp.cwd(part)
        except all_errors:
            ftp.mkd(part)
            ftp.cwd(part)


def _local_files(root):
    root = Path(root).resolve()
    files = []
    for path in root.rglob("*"):
        if not path.is_file() or path.is_symlink():
            continue
        try:
            path.resolve().relative_to(root)
        except ValueError:
            continue
        files.append(path)
    return sorted(files, key=lambda item: item.relative_to(root).as_posix().lower())


def upload_folder(
    host,
    username,
    password,
    local_folder,
    remote_folder="hotspot",
    port=21,
    progress=None,
):
    local = Path(local_folder).resolve()
    if not local.is_dir():
        raise FileNotFoundError(local)
    validate_hotspot_template(local)
    files = _local_files(local)
    if not files:
        raise HotspotTransferError("مجلد القالب فارغ.")
    remote_parts = _remote_directory_parts(remote_folder)
    remote_folder = "/".join(remote_parts)
    for path in files:
        _remote_parts(path.relative_to(local).as_posix())

    ftp = _connect(host, username, password, port)
    count = 0
    try:
        for path in files:
            relative = path.relative_to(local).as_posix()
            parent = posixpath.dirname(relative)
            remote_parent = posixpath.join(str(remote_folder).strip("/"), parent)
            _ensure_remote_dir(ftp, remote_parent)
            with path.open("rb") as handle:
                ftp.storbinary("STOR " + path.name, handle, blocksize=64 * 1024)
            count += 1
            if progress:
                progress(count, len(files), relative)
        return count
    except all_errors as exc:
        raise HotspotTransferError(
            "توقف رفع الصفحات عند الملف رقم %d: %s" % (count + 1, exc)) from exc
    finally:
        _close(ftp)


def _walk_mlsd(ftp, remote, depth=0):
    if depth > 32:
        raise HotspotTransferError("تجاوز مجلد FTP عمق المجلدات المسموح.")
    result = []
    for name, facts in ftp.mlsd(remote):
        if name in (".", ".."):
            continue
        if len(_remote_parts(name)) != 1 or '/' in name or '\\' in name:
            raise HotspotTransferError("اسم غير آمن في قائمة FTP.")
        path = posixpath.join(remote.rstrip("/"), name)
        item_type = facts.get("type", "")
        if item_type == "dir":
            result.extend(_walk_mlsd(ftp, path, depth+1))
        elif item_type == "file":
            result.append(path)
    return result


def _walk_nlst(ftp, remote, depth=0):
    """Fallback for RouterOS FTP servers without MLSD support."""
    if depth > 32:
        raise HotspotTransferError("تجاوز مجلد FTP عمق المجلدات المسموح.")
    result = []
    original = ftp.pwd()
    try:
        ftp.cwd(remote)
        names = ftp.nlst()
        for raw_name in names:
            name = posixpath.basename(str(raw_name).rstrip("/"))
            if name in ("", ".", ".."):
                continue
            _remote_parts(name)
            path = posixpath.join(remote.rstrip("/"), name)
            is_directory = False
            try:
                ftp.cwd(path)
                is_directory = True
            except all_errors:
                is_directory = False
            finally:
                with contextlib.suppress(all_errors):
                    ftp.cwd(remote)
            if is_directory:
                result.extend(_walk_nlst(ftp, path, depth+1))
            else:
                result.append(path)
        return result
    finally:
        with contextlib.suppress(all_errors):
            ftp.cwd(original)


def _walk_ftp(ftp, remote):
    try:
        return _walk_mlsd(ftp, remote)
    except HotspotTransferError:
        raise
    except all_errors:
        return _walk_nlst(ftp, remote)


def _safe_destination(root, relative):
    parts = _remote_parts(relative)
    destination = root.joinpath(*parts).resolve()
    if os.path.commonpath([str(root.resolve()), str(destination)]) != str(root.resolve()):
        raise HotspotTransferError("رفض مسار تنزيل غير آمن: %s" % relative)
    return destination


def download_folder(
    host,
    username,
    password,
    local_folder,
    remote_folder="hotspot",
    port=21,
    progress=None,
):
    local = Path(local_folder).resolve()
    local.mkdir(parents=True, exist_ok=True)
    remote_root = "/" + "/".join(_remote_directory_parts(remote_folder))
    ftp = _connect(host, username, password, port)
    try:
        files = _walk_ftp(ftp, remote_root)
        if not files:
            raise HotspotTransferError("لم يتم العثور على ملفات داخل %s في الراوتر." % remote_root)
        for index, remote_path in enumerate(files, 1):
            prefix = remote_root.rstrip("/") + "/"
            relative = remote_path[len(prefix) :] if remote_path.startswith(prefix) else posixpath.basename(remote_path)
            destination = _safe_destination(local, relative)
            destination.parent.mkdir(parents=True, exist_ok=True)
            temporary = tempfile.NamedTemporaryFile(prefix='.nidi-download-',dir=str(destination.parent),delete=False)
            partial = Path(temporary.name)
            try:
                with temporary as handle:
                    ftp.retrbinary("RETR " + remote_path, handle.write, blocksize=64 * 1024)
                partial.replace(destination)
            except Exception:
                with contextlib.suppress(OSError):
                    partial.unlink()
                raise
            if progress:
                progress(index, len(files), relative)
        return len(files)
    except HotspotTransferError:
        raise
    except all_errors as exc:
        raise HotspotTransferError("فشل تنزيل صفحات الهوتسبوت: %s" % exc) from exc
    finally:
        _close(ftp)
