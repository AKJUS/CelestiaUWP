﻿#pragma once

#include <celengine/selection.h>
#include "CelestiaAstroObject.h"
#include "CelestiaBody.h"
#include "CelestiaSelection.g.h"

namespace winrt::CelestiaComponent::implementation
{
    struct CelestiaSelection : CelestiaSelectionT<CelestiaSelection>
    {
        CelestiaSelection(CelestiaComponent::CelestiaAstroObject const& obj);
        CelestiaSelection(Selection const& sel);

        CelestiaComponent::CelestiaBody Body();

        ~CelestiaSelection();

    private:
        Selection* s;
    };
}

namespace winrt::CelestiaComponent::factory_implementation
{
    struct CelestiaSelection : CelestiaSelectionT<CelestiaSelection, implementation::CelestiaSelection>
    {
    };
}
