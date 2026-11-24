#!/usr/bin/env python3
"""Reference implementation that generates protocol/test-vectors.json.

Independent of the Kotlin and C# cores: both must reproduce these values.
Requires the `cryptography` package. Run from the repo root:

    python3 tools/gen_test_vectors.py > protocol/test-vectors.json
"""
import base64
import hashlib
import hmac
import json
import sys
import unicodedata

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

ZERO_SALT = bytes(32)


def b64(b: bytes) -> str:
    return base64.b64encode(b).decode("ascii")


def sha256(b: bytes) -> bytes:
    return hashlib.sha256(b).digest()


def hkdf(ikm: bytes, salt: bytes, info: str, length: int = 32) -> bytes:
    return HKDF(algorithm=hashes.SHA256(), length=length, salt=salt, info=info.encode()).derive(ikm)


def normalize(s: str) -> str:
    return unicodedata.normalize("NFC", s.replace("\r\n", "\n").replace("\r", "\n"))


def content_hash(s: str) -> str:
    return sha256(normalize(s).encode("utf-8")).hex()


def is_blank(s: str) -> bool:
    return all(c.isspace() for c in s)


def work_keys(pair_secret: bytes) -> dict:
    return {
        "macKey": hkdf(pair_secret, ZERO_SALT, "klip-mac").hex(),
        "encKey": hkdf(pair_secret, ZERO_SALT, "klip-enc").hex(),
    }


def pub_bytes(priv: ec.EllipticCurvePrivateKey) -> bytes:
    return priv.public_key().public_bytes(
        serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint
    )


def pin(pub_i: bytes, pub_r: bytes, nonce_i: bytes, nonce_r: bytes) -> str:
    h = sha256(pub_i + pub_r + nonce_i + nonce_r)
    return f"{int.from_bytes(h[:4], 'big') % 1_000_000:06d}"


def canonical(method: str, path: str, ts: str, nonce: str, body: bytes) -> str:
    return "\n".join([method, path, ts, nonce, sha256(body).hex()])


def normalization_vectors():
    inputs = [
        "hola mundo",
        "hola\r\nmundo\r\n",
        "linea1\rlinea2",
        "mixto\r\n\r\nfin\n",
        "café",           # NFD é -> NFC
        "café",            # already NFC
        "emoji \U0001F600 ❤️",
        "https://example.com/a?b=1&c=2",
        "  espacios alrededor  ",
        "",
        "   ",
        " \t\r\n ",
        "  ",         # NBSP + EM SPACE
        "​",               # zero-width space is NOT white space
    ]
    out = []
    for s in inputs:
        out.append({
            "input": s,
            "normalized": normalize(s),
            "contentHash": content_hash(s),
            "isBlank": is_blank(s),
        })
    return out


def pairing_vector():
    d_i = 0x1F2E3D4C5B6A79881F2E3D4C5B6A79881F2E3D4C5B6A79881F2E3D4C5B6A7988
    d_r = 0x0123456789ABCDEFFEDCBA98765432100123456789ABCDEFFEDCBA9876543210
    priv_i = ec.derive_private_key(d_i, ec.SECP256R1())
    priv_r = ec.derive_private_key(d_r, ec.SECP256R1())
    pub_i, pub_r = pub_bytes(priv_i), pub_bytes(priv_r)
    nonce_i = bytes(range(0x00, 0x20))
    nonce_r = bytes(range(0x20, 0x40))

    shared = priv_i.exchange(ec.ECDH(), priv_r.public_key())
    assert shared == priv_r.exchange(ec.ECDH(), priv_i.public_key())
    pair_secret = hkdf(shared, nonce_i + nonce_r, "klip-pair-v1")

    return {
        "initiatorPrivate": f"{d_i:064x}",
        "initiatorPublic": b64(pub_i),
        "responderPrivate": f"{d_r:064x}",
        "responderPublic": b64(pub_r),
        "initiatorNonce": b64(nonce_i),
        "responderNonce": b64(nonce_r),
        "sharedSecret": shared.hex(),
        "pairSecret": pair_secret.hex(),
        **work_keys(pair_secret),
        "pin": pin(pub_i, pub_r, nonce_i, nonce_r),
    }


def random_secret_vector():
    pair_secret = bytes.fromhex("a5" * 32)
    return {"pairSecret": pair_secret.hex(), **work_keys(pair_secret)}


def invalid_public_keys():
    pub = base64.b64decode(pairing_vector()["initiatorPublic"])
    off_curve = bytearray(pub)
    off_curve[-1] ^= 0x01
    compressed = bytes([0x02]) + pub[1:33]
    return [
        {"reason": "point not on curve", "publicKey": b64(bytes(off_curve))},
        {"reason": "compressed encoding", "publicKey": b64(compressed)},
        {"reason": "wrong length", "publicKey": b64(pub[:64])},
        {"reason": "all zeros", "publicKey": b64(bytes([0x04]) + bytes(64))},
    ]


def signature_vectors():
    mac_key = bytes.fromhex(random_secret_vector()["macKey"])
    cases = [
        ("POST", "/clipboard", "1763930000000", b64(bytes(range(16))), b'{"protocolVersion":1}'),
        ("GET", "/ping", "1763930012345", b64(bytes(range(16, 32))), b""),
        ("POST", "/unpair", "1763930099999", b64(bytes([0xFF] * 16)), "{\"x\":\"ñ€\U0001F600\"}".encode()),
    ]
    out = []
    for method, path, ts, nonce, body in cases:
        can = canonical(method, path, ts, nonce, body)
        sig = hmac.new(mac_key, can.encode(), hashlib.sha256).digest()
        out.append({
            "macKey": mac_key.hex(),
            "method": method,
            "path": path,
            "timestamp": ts,
            "nonce": nonce,
            "body": b64(body),
            "canonical": can,
            "signature": b64(sig),
        })
    return out


def aes_gcm_vectors():
    enc_key = bytes.fromhex(random_secret_vector()["encKey"])
    cases = [
        (bytes(12), "id-1\ndevice-a", "{\"type\":\"text\",\"content\":\"hola\"}"),
        (bytes(range(12)), "", "plaintext with ñ, € and \U0001F600"),
        (bytes([0xAB] * 12), "x\ny", ""),
    ]
    out = []
    for iv, aad, pt in cases:
        ct = AESGCM(enc_key).encrypt(iv, pt.encode(), aad.encode())
        out.append({
            "encKey": enc_key.hex(),
            "iv": b64(iv),
            "aad": aad,
            "plaintext": pt,
            "ciphertext": b64(ct),
        })
    return out


def envelope_vectors():
    enc_key = bytes.fromhex(random_secret_vector()["encKey"])
    message_id = "3f1c9d2e-8b7a-4c6d-9e0f-112233445566"
    origin = "device-android-pixel"
    content = "Línea 1\r\nLínea 2 \U0001F600"
    payload = {
        "type": "text",
        "content": content,
        "contentHash": content_hash(content),
        "originDeviceName": "Pixel",
        "isTest": False,
    }
    iv = bytes(range(100, 112))
    aad = f"{message_id}\n{origin}".encode()
    pt = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode()
    ct = AESGCM(enc_key).encrypt(iv, pt, aad)
    envelope = {
        "protocolVersion": 1,
        "messageId": message_id,
        "originDeviceId": origin,
        "timestamp": 1763930000000,
        "iv": b64(iv),
        "ciphertext": b64(ct),
    }

    # Tampered variants that must be rejected.
    bad_hash_payload = dict(payload, contentHash="00" * 32)
    bad_pt = json.dumps(bad_hash_payload, ensure_ascii=False, separators=(",", ":")).encode()
    bad_hash_env = dict(envelope, ciphertext=b64(AESGCM(enc_key).encrypt(iv, bad_pt, aad)))
    flipped = bytearray(ct)
    flipped[0] ^= 0x01

    return {
        "encKey": enc_key.hex(),
        "valid": {"envelope": envelope, "payload": payload},
        "mustReject": [
            {"reason": "tampered ciphertext", "envelope": dict(envelope, ciphertext=b64(bytes(flipped)))},
            {"reason": "originDeviceId changed (AAD mismatch)", "envelope": dict(envelope, originDeviceId="device-evil")},
            {"reason": "messageId changed (AAD mismatch)", "envelope": dict(envelope, messageId="00000000-0000-4000-8000-000000000000")},
            {"reason": "contentHash does not match content", "envelope": bad_hash_env},
            {"reason": "unsupported protocolVersion", "envelope": dict(envelope, protocolVersion=2)},
        ],
    }


def introduction_vector():
    enc_key = bytes.fromhex(random_secret_vector()["encKey"])
    introducer, member_id, issued_at = "device-introducer", "device-member", 1763930012345
    secret = bytes(range(32))
    iv = bytes(range(200, 212))
    aad = f"/group/introduce\n{introducer}\n{member_id}\n{issued_at}".encode()
    return {
        "encKey": enc_key.hex(),
        "introducerId": introducer,
        "memberId": member_id,
        "issuedAt": issued_at,
        "iv": b64(iv),
        "secret": secret.hex(),
        "aad": aad.decode(),
        "ciphertext": b64(AESGCM(enc_key).encrypt(iv, secret, aad)),
    }


def main():
    vectors = {
        "description": "Klip protocol v1 test vectors. Generated by tools/gen_test_vectors.py; do not edit by hand.",
        "normalization": normalization_vectors(),
        "pairing": pairing_vector(),
        "randomSecret": random_secret_vector(),
        "invalidPublicKeys": invalid_public_keys(),
        "signatures": signature_vectors(),
        "aesGcm": aes_gcm_vectors(),
        "envelope": envelope_vectors(),
        "introduction": introduction_vector(),
    }
    json.dump(vectors, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
