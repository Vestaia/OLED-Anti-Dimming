// SPDX-License-Identifier: GPL-3.0-only
#include "filter_activation.hpp"
#include <cassert>
int main() {
    FilterActivation activation;
    assert(!activation.ready(10000));
    activation.activate(10000);
    assert(!activation.ready(10499));
    assert(activation.ready(10500));
    activation.reset();
    assert(!activation.ready(20000));
    activation.activate(20000);
    assert(!activation.ready(20499));
    assert(activation.ready(20500));
}
