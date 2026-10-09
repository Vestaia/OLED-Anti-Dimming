// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <map>
#include <string>
#include <stdexcept>
#include <cmath>
#include <vector>
struct HsvZeroRegions {
    struct Region {double hue,saturation,brightness,area;};
    std::vector<Region> regions;
    bool skip(double hue,double saturation,double brightness,double area,bool pruneLowerSaturation=true) const {
        if(brightness==0)return true;
        for(const auto& z:regions)if((saturation==0 || std::abs(hue-z.hue)<1e-6) && (pruneLowerSaturation?saturation<=z.saturation+1e-9:std::abs(saturation-z.saturation)<1e-9) && brightness<=z.brightness+1e-9 && area<=z.area+1e-9)return true;
        return false;
    }
    void observe(double hue,double saturation,double brightness,double area){regions.push_back({hue,saturation,brightness,area});}
};
struct FlatSweep
{
    static bool smallCorrection(double signal, double reference)
    {
        return reference > 0 && std::abs(signal / reference - 1) <= .01 + 1e-9;
    }
    struct State
    {
        int consecutive = 0;
        double lastArea = 2, cutoff = 0;
    };
    std::map<std::string, State> groups;
    std::map<std::string, double> brightnessCutoffs;
    void observeBrightness(const std::string &family, double level, double area, bool flat)
    {
        if (!family.empty() && area >= .999999 && flat)
            brightnessCutoffs[family] = (std::max)(brightnessCutoffs[family], level);
    }
    bool skipBrightness(const std::string &family, double level) const
    {
        auto it = brightnessCutoffs.find(family);
        return !family.empty() && it != brightnessCutoffs.end() && level < it->second;
    }
    void observe(const std::string &group, double area, bool flat)
    {
        if (group.empty())
            return;
        auto &state = groups[group];
        if (area >= state.lastArea)
            state.consecutive = 0;
        state.consecutive = flat ? state.consecutive + 1 : 0;
        state.lastArea = area;
        state.cutoff = flat ? area : 0;
    }
    bool skip(const std::string &group, double area) const
    {
        auto it = groups.find(group);
        return !group.empty() && it != groups.end() && it->second.cutoff > 0 &&
               area < it->second.cutoff;
    }
};
inline void flatSweepSelfTest()
{
    if (!FlatSweep::smallCorrection(252.5, 250) || !FlatSweep::smallCorrection(247.5, 250) ||
        FlatSweep::smallCorrection(253, 250))
        throw std::runtime_error("One-percent correction boundary failed");
    FlatSweep sweep;
    sweep.observeBrightness("gray", 37.5, 1, true);
    if (!sweep.skipBrightness("gray", 25) || sweep.skipBrightness("red", 25) ||
        sweep.skipBrightness("gray", 37.5))
        throw std::runtime_error("Brightness cutoff isolation failed");
    sweep.observeBrightness("red", 100, .4, true);
    if (sweep.skipBrightness("red", 50))
        throw std::runtime_error("Partial area established a brightness cutoff");
    sweep.observe("white", 1, false);
    sweep.observe("white", .7, true);
    if (!sweep.skip("white", .4))
        throw std::runtime_error("One flat sample did not establish the lower zero region");
    sweep.observe("white", .4, true);
    if (!sweep.skip("white", .25) || sweep.skip("white", .4) || sweep.skip("red", .25) ||
        sweep.skip("", .25))
        throw std::runtime_error("Flat sweep group/area boundary failed");
    FlatSweep irregular;
    irregular.observe("x", 1, true);
    irregular.observe("x", .7, false);
    if (irregular.skip("x", .25))
        throw std::runtime_error("Nonflat response failed to clear the zero region");
    irregular.observe("x", .4, true);
    if (!irregular.skip("x", .25))
        throw std::runtime_error("Flat response did not restore the zero region");
}
