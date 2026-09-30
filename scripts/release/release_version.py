"""The deliberately narrow release-version policy shared by both entrypoints."""

import re


_CORE = r"(?:0|[1-9][0-9]*)"
_VERSION = re.compile(
    rf"v{_CORE}\.{_CORE}\.{_CORE}(?P<internal>-internal\.[1-9][0-9]*)?")


def prerelease_for_version(version):
    """Return True for internal, False for normal, or None for invalid input.

    The suffix classifies the candidate; it never grants publication authority
    or changes draft visibility. Public preview channels are not supported.
    """
    match = _VERSION.fullmatch(version) if isinstance(version, str) else None
    return match.group("internal") is not None if match else None
