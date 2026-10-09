// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstdint>

// Access under filterMutex. Eligibility is blocked independently of this timer.
class FilterActivation {
    std::uint64_t started = 0;
    bool armed = false;
public:
    void reset() { armed = false; }
    void activate(std::uint64_t now) { started = now; armed = true; }
    bool ready(std::uint64_t now) const {
        return armed && now >= started && now - started >= 500;
    }
};
