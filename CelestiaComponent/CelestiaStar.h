//
// Copyright © 2021 Celestia Development Team. All rights reserved.
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.
//

#pragma once

#include <celengine/star.h>
#include "CelestiaAstroObject.h"
#include "CelestiaStar.g.h"

namespace CelestiaComponent
{
    struct CelestiaUniversalCoord;
}

namespace winrt::CelestiaComponent::implementation
{
    struct CelestiaStar : CelestiaStarT<CelestiaStar, CelestiaAstroObject>
    {
        CelestiaStar(Star* star);
        hstring InfoURL();
        CelestiaComponent::CelestiaUniversalCoord PositionAtTime(Windows::Foundation::DateTime const& time);
        hstring SpectralType();
    };
}
