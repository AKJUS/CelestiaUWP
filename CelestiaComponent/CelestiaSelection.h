//
// Copyright © 2021 Celestia Development Team. All rights reserved.
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.
//

#pragma once

#include <celengine/selection.h>
#include "CelestiaAstroObject.h"
#include "CelestiaSelection.g.h"

namespace winrt::CelestiaComponent::implementation
{
    struct CelestiaSelection : CelestiaSelectionT<CelestiaSelection>
    {
        CelestiaSelection(CelestiaComponent::CelestiaAstroObject const& obj);
        CelestiaSelection(Selection const& sel);

        CelestiaComponent::CelestiaAstroObject Object();
        bool IsEmpty();
        double Radius();

        ~CelestiaSelection();

        Selection* s;
    };
}

namespace winrt::CelestiaComponent::factory_implementation
{
    struct CelestiaSelection : CelestiaSelectionT<CelestiaSelection, implementation::CelestiaSelection>
    {
    };
}
