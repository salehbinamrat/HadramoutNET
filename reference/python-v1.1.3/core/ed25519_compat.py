"""Small dependency-free Ed25519 implementation for the offline license tool.

The arithmetic follows RFC 8032. It is intentionally kept local to this
utility so the Windows portable runtime needs no pip package or DLL.
"""
from __future__ import annotations

import hashlib
import hmac
import secrets
from typing import Optional, Tuple


_Q = 2 ** 255 - 19
_L = 2 ** 252 + 27742317777372353535851937790883648493
_D = (-121665 * pow(121666, _Q - 2, _Q)) % _Q
_I = pow(2, (_Q - 1) // 4, _Q)
_B_Y = (4 * pow(5, _Q - 2, _Q)) % _Q
_IDENTITY = (0, 1)


def _inv(value: int) -> int:
    return pow(value, _Q - 2, _Q)


def _x_recover(y: int) -> int:
    xx = (y * y - 1) * _inv(_D * y * y + 1) % _Q
    x = pow(xx, (_Q + 3) // 8, _Q)
    if (x * x - xx) % _Q != 0:
        x = x * _I % _Q
    if x & 1:
        x = _Q - x
    return x


_BASE = (_x_recover(_B_Y), _B_Y)


def _add(left: Tuple[int, int], right: Tuple[int, int]) -> Tuple[int, int]:
    x1, y1 = left
    x2, y2 = right
    product = _D * x1 * x2 * y1 * y2 % _Q
    x3 = (x1 * y2 + x2 * y1) * _inv(1 + product) % _Q
    y3 = (y1 * y2 + x1 * x2) * _inv(1 - product) % _Q
    return x3, y3


def _multiply(point: Tuple[int, int], scalar: int) -> Tuple[int, int]:
    # RFC 8032 section 5.1.4: extended coordinates need only one inversion
    # per multiplication, instead of two per addition. Wire format and strict
    # subgroup/canonical checks below remain unchanged. Python is not constant-time.
    if scalar < 0:
        raise ValueError("Ed25519 scalar must be non-negative")
    result = (0, 1, 1, 0)
    x, y = point
    addend = (x, y, 1, x * y % _Q)
    while scalar:
        if scalar & 1:
            result = _add_extended(result, addend)
        addend = _add_extended(addend, addend)
        scalar >>= 1
    x, y, z, _ = result
    inverse = _inv(z)
    return x * inverse % _Q, y * inverse % _Q


def _add_extended(left, right):
    x1, y1, z1, t1 = left
    x2, y2, z2, t2 = right
    a = (y1 - x1) * (y2 - x2) % _Q
    b = (y1 + x1) * (y2 + x2) % _Q
    c = 2 * _D * t1 * t2 % _Q
    d = 2 * z1 * z2 % _Q
    e, f, g, h = b - a, d - c, d + c, b + a
    return e * f % _Q, g * h % _Q, f * g % _Q, e * h % _Q


def _encode(point: Tuple[int, int]) -> bytes:
    x, y = point
    return (y | ((x & 1) << 255)).to_bytes(32, "little")


def _decode(data: bytes) -> Optional[Tuple[int, int]]:
    if len(data) != 32:
        return None
    value = int.from_bytes(data, "little")
    y = value & ((1 << 255) - 1)
    if y >= _Q:
        return None
    x = _x_recover(y)
    if (y * y - x * x - 1 - _D * x * x * y * y) % _Q != 0:
        return None
    if x == 0 and value >> 255:
        return None
    if (x & 1) != (value >> 255):
        x = _Q - x
    return x, y


def public_key_from_seed(seed: bytes) -> bytes:
    if len(seed) != 32:
        raise ValueError("Ed25519 seed must be 32 bytes")
    digest = hashlib.sha512(seed).digest()
    scalar = int.from_bytes(digest[:32], "little")
    scalar &= (1 << 254) - 8
    scalar |= 1 << 254
    return _encode(_multiply(_BASE, scalar))


def generate_keypair() -> Tuple[bytes, bytes]:
    seed = secrets.token_bytes(32)
    return seed, public_key_from_seed(seed)


def sign(message: bytes, seed: bytes) -> bytes:
    if len(seed) != 32:
        raise ValueError("Ed25519 seed must be 32 bytes")
    digest = hashlib.sha512(seed).digest()
    scalar = int.from_bytes(digest[:32], "little")
    scalar &= (1 << 254) - 8
    scalar |= 1 << 254
    prefix = digest[32:]
    public = _encode(_multiply(_BASE, scalar))
    nonce = int.from_bytes(hashlib.sha512(prefix + message).digest(), "little") % _L
    encoded_r = _encode(_multiply(_BASE, nonce))
    challenge = int.from_bytes(hashlib.sha512(encoded_r + public + message).digest(), "little") % _L
    response = (nonce + challenge * scalar) % _L
    return encoded_r + response.to_bytes(32, "little")


def verify(signature: bytes, message: bytes, public: bytes) -> bool:
    if not all(isinstance(value, bytes) for value in (signature, message, public)):
        return False
    if len(signature) != 64 or len(public) != 32:
        return False
    point_r = _decode(signature[:32])
    point_a = _decode(public)
    response = int.from_bytes(signature[32:], "little")
    if point_r is None or point_a is None or response >= _L:
        return False
    # Licenses use ordinary prime-order Ed25519 keys; reject degenerate keys.
    if point_a == _IDENTITY or _multiply(point_a, _L) != _IDENTITY:
        return False
    if point_r == _IDENTITY or _multiply(point_r, _L) != _IDENTITY:
        return False
    challenge = int.from_bytes(hashlib.sha512(signature[:32] + public + message).digest(), "little") % _L
    left = _multiply(_BASE, response)
    right = _add(point_r, _multiply(point_a, challenge))
    return hmac.compare_digest(_encode(left), _encode(right))
