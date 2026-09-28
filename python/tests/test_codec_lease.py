"""GA-U-64.py — the Python codec and lease rule match GA-U-01's and GA-U-06's vectors (plus GA-U-03…05)."""

from __future__ import annotations

import pytest

from generic_axis_check.lease import evaluate
from generic_axis_check.registers import RegisterMap, StatusBlock, from_words, next_nonzero, to_words


def test_to_words_minus_2_500_000_is_low_word_first() -> None:
    # GA-U-01: −2 500 000 packs to [0xDA60, 0xFFD9] and unpacks back.
    assert to_words(-2_500_000) == (0xDA60, 0xFFD9)
    assert from_words(0xDA60, 0xFFD9) == -2_500_000


@pytest.mark.parametrize(
    ("value", "words"),
    [(65538, (2, 1)), (-2, (0xFFFE, 0xFFFF)), (0, (0, 0)), (2**31 - 1, (0xFFFF, 0x7FFF)), (-(2**31), (0, 0x8000))],
)
def test_to_words_round_trips_the_edges(value: int, words: tuple[int, int]) -> None:
    assert to_words(value) == words
    assert from_words(*words) == value


@pytest.mark.parametrize("value", [2**31, -(2**31) - 1])
def test_to_words_outside_int32_raises(value: int) -> None:
    with pytest.raises(ValueError, match="int32"):
        to_words(value)


@pytest.mark.parametrize(
    ("owner", "age", "granted"),
    [(0, 0.0, True), (0, 99.0, True), (7, 0.0, True), (3, 0.2, False), (3, 1.0, True), (3, 5.0, True)],
)
def test_lease_evaluate_matches_ga_u_06_vectors(owner: int, age: float, granted: bool) -> None:
    assert evaluate(owner, age, 1.0, 7) is granted


def test_status_block_parses_every_field() -> None:
    # GA-U-03
    regs = [
        3,
        0b11,
        *to_words(-1234),
        *to_words(500),
        4,
        42,
        *to_words(-10),
        *to_words(10_000_000),
        *to_words(500_000),
        1,
    ]
    s = StatusBlock.parse(regs)
    assert (s.state, s.actual_position, s.actual_velocity, s.fault_code, s.command_ack) == (3, -1234, 500, 4, 42)
    assert (s.travel_min, s.travel_max, s.max_velocity, s.map_version) == (-10, 10_000_000, 500_000, 1)
    assert s.homed
    assert s.in_position


def test_status_block_with_14_registers_raises() -> None:
    with pytest.raises(ValueError, match="15"):
        StatusBlock.parse([0] * 14)


def test_register_map_bases_move_whole_blocks() -> None:
    # GA-U-04
    m = RegisterMap(200, 300)
    assert (m.heartbeat, m.map_version, m.status + 7) == (208, 314, 307)
    with pytest.raises(ValueError, match="overlaps"):
        RegisterMap(95, 100)
    with pytest.raises(ValueError, match="outside"):
        RegisterMap(0, 65530)


def test_next_nonzero_wraps_65535_to_1() -> None:
    # GA-U-05
    assert next_nonzero(65535) == 1
    assert next_nonzero(0) == 1
    assert next_nonzero(41) == 42
