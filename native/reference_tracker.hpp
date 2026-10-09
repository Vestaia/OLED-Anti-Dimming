// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cmath>
#include <cstdint>
#include <stdexcept>
struct ReferenceTracker {
    double level = 0;
    std::uint64_t checked = 0;
    bool due(std::uint64_t now) const { return now >= checked && now - checked >= 20000; }
    bool update(double value, std::uint64_t now) {
        bool warning = level > 0 && std::abs(value - level) > .05 * level;
        level = value; checked = now;
        return warning;
    }
};
inline void referenceTrackerSelfTest() {
    ReferenceTracker reference;
    reference.update(60, 100);
    if(reference.due(20099) || !reference.due(20100)) throw std::runtime_error("Reference interval failed");
    if(reference.update(61, 20100) || reference.level != 61 || reference.due(20101)) throw std::runtime_error("Reference drift was not tracked");
    if(!reference.update(65, 40100) || reference.level != 65) throw std::runtime_error("Large reference change did not warn and update");
}
