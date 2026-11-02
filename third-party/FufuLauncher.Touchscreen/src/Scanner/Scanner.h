/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the AGPL-3.0 License.
*/
#pragma once
#include <cstddef>
#include <string>

namespace Scanner
{
    // First executable-section match, retaining the pinned upstream selection order.
    void* ScanFirstMainMod(const std::string& signature);
}
