﻿#include "pch.h"
#include <celengine/astro.h>
#include "CelestiaHelper.h"
#if __has_include("CelestiaHelper.g.cpp")
#include "CelestiaHelper.g.cpp"
#endif

using namespace std;

namespace winrt::CelestiaComponent::implementation
{
	double CelestiaHelper::JulianDayFromDateTime(Windows::Foundation::DateTime const& dateTime)
	{
		Windows::Globalization::Calendar c;
		c.ChangeClock(Windows::Globalization::ClockIdentifiers::TwentyFourHour());
		c.ChangeCalendarSystem(Windows::Globalization::CalendarIdentifiers::Gregorian());
		c.ChangeTimeZone(L"GMT");
		c.SetDateTime(dateTime);
		int era = c.Era();
		int year = c.Year();
		if (era < 1) year = 1 - year;
		astro::Date astroDate(year, c.Month(), c.Day());
		astroDate.hour = c.Hour();
		astroDate.minute = c.Minute();
		astroDate.seconds = c.Second();
		return astro::UTCtoTDB(astroDate);
	}

	Windows::Foundation::DateTime CelestiaHelper::DateTimeFromJulianDay(double julianDay)
	{
		std::vector<hstring> lang;
		lang.push_back(to_hstring("en"));
		Windows::Globalization::Calendar c;
		c.ChangeClock(Windows::Globalization::ClockIdentifiers::TwentyFourHour());
		c.ChangeCalendarSystem(Windows::Globalization::CalendarIdentifiers::Gregorian());
		c.ChangeTimeZone(L"GMT");
		astro::Date astroDate(julianDay);
		int year = astroDate.year;

		int era = 1;
		if (year < 1)
		{
			era = 0;
			year = 1 - year;
		}
		c.Era(era);
		c.Year(year);
		c.Month(astroDate.month);
		c.Day(astroDate.day);
		c.Hour(astroDate.hour);
		c.Minute(astroDate.minute);
		c.Second(floor(astroDate.seconds));
		return c.GetDateTime();
	}
}
