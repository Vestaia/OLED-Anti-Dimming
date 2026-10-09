// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include "match.hpp"
#include <limits>

struct MatchProgress {
    double previousError = std::numeric_limits<double>::infinity();
    int stagnant = 0;
    bool stalled(double error, double tolerance) {
        if (error < previousError - tolerance / 16) stagnant = 0;
        else ++stagnant;
        previousError = error;
        return stagnant >= 4;
    }
};

// PI-like control in log signal. Camera codes supply ordering and a measured
// local slope, never a luminance ratio. The caller enforces the measured delay
// plus response horizon before feeding back each applied command.
struct ContinuousMatcher : ObservationMatcher
{
    MatchProgress progress;
    double integral = 0, previousSignal = 0, previousObserved = 0, slope = 20;
    int plateauConfirmations = 0;
    double gainScale = 1, stepLimit = .25;
    double lastError = 0;
    bool haveError = false;
    Result update(double observed, double target, double tolerance, double dt = .2)
    {
        double error = target - observed;
        double logSignal = std::log(std::max(trial, .0001));
        if (previousSignal > 0)
        {
            double delta = logSignal - std::log(previousSignal);
            if (std::abs(delta) > .0002 && std::abs(observed - previousObserved) > tolerance)
            {
                double measured = (observed - previousObserved) / delta;
                if (measured > 1 && std::isfinite(measured))
                    slope = .25 * slope + .75 * std::clamp(measured, 1., 500.);
            }
        }
        previousSignal = trial;
        previousObserved = observed;
        if (std::abs(error) <= tolerance)
        {
            integral = 0;
            return Matched;
        }
        // Tune damping using only observations of commands whose measured
        // delay/response horizon has elapsed. Never integrate stale frames.
        if (haveError)
        {
            if (lastError * error < 0 || std::abs(error) > std::abs(lastError) * 1.05)
            {
                gainScale = std::max(.2, gainScale * .6);
                integral = 0;
            }
            else if (std::abs(error) < std::abs(lastError) * .85)
                gainScale = std::min(1.3, gainScale * 1.12);
        }
        lastError = error;
        haveError = true;
        double floor = std::max(.12, tolerance * .5);
        if (observed > bestObserved + floor)
        {
            previousBestSignal = bestSignal;
            bestSignal = trial;
            bestObserved = observed;
            plateauConfirmations = 0;
        }
        if (error > 0 && high == 0 && trial > ascendingSignal)
        {
            if (ascendingSignal > 0 && trial / ascendingSignal >= 1.08 &&
                std::abs(observed - ascendingObserved) <= floor)
                ++plateauConfirmations;
            else
                plateauConfirmations = 0;
            ascendingSignal = trial;
            ascendingObserved = observed;
            if (plateauConfirmations >= 2)
                return Saturated;
        }
        if (progress.stalled(std::abs(error), tolerance)) return Limit;
        if (++iterations > 45)
            return Limit;
        if (error > 0)
            low = trial;
        else
            high = trial;
        // Limited integral and proportional step prevent windup and delay overshoot.
        integral =
            std::clamp(integral + error * std::clamp(dt, .02, 1.), -slope * .25, slope * .25);
        double step =
            std::clamp(gainScale * (.8 * error + .02 * integral) / slope, -stepLimit, stepLimit);
        double next = std::clamp(logSignal + step, std::log(.0001), std::log(10000.));
        if (low > 0 && high > 0)
        {
            double lo = std::log(low), hi = std::log(high);
            next = std::clamp(next, lo + .1 * (hi - lo), hi - .1 * (hi - lo));
        }
        if (std::abs(next - logSignal) < 1e-8)
            return Unreachable;
        trial = std::exp(next);
        return More;
    }
};

// Final control uses one non-overlapping, settled single-frame observation per
// command. No integral state carries over from the fast controller.
struct FineWindowMatcher : ObservationMatcher
{
    MatchProgress progress;
    int flatWindows = 0;
    double step = .002;
    Result update(double observed, double target, double tolerance)
    {
        if (std::abs(observed - target) <= tolerance)
            return Matched;
        if (++iterations > 24)
            return Limit;
        if (observed < target && high == 0)
        {
            if (ascendingSignal == 0)
            {
                ascendingSignal = trial;
                ascendingObserved = observed;
            }
            else if (trial / ascendingSignal >= 1.02)
            {
                if (std::abs(observed - ascendingObserved) <= std::max(.12, tolerance * .5))
                    ++flatWindows;
                else
                    flatWindows = 0;
                ascendingSignal = trial;
                ascendingObserved = observed;
                if (flatWindows >= 2)
                    return Saturated;
            }
        }
        if (progress.stalled(std::abs(observed - target), tolerance)) return Limit;
        if (observed < target)
            low = trial;
        else
            high = trial;
        double next;
        if (low > 0 && high > 0)
            next = std::sqrt(low * high);
        else
            next = trial * std::exp(observed < target ? step : -step);
        next = trial * std::exp(std::clamp(std::log(next / trial), -.005, .005));
        next = std::clamp(next, .0001, 10000.);
        if (std::abs(std::log(next / trial)) < 1e-7)
            return Limit;
        trial = next;
        return More;
    }
};

inline void fineWindowMatcherSelfTest()
{
    MatchProgress stalled;
    for (int i=0;i<4;i++)
        if (stalled.stalled(1., .04)) throw std::runtime_error("Progress stopped too early");
    if (!stalled.stalled(1., .04)) throw std::runtime_error("Progress did not stop after four adjustments");
    for (double gamma : {.35, 1., 2.2})
    {
        FineWindowMatcher controller;
        controller.trial = 104;
        bool matched = false;
        for (int window = 0; window < 30; window++)
        {
            double observed = 60 * std::pow(controller.trial / 100, gamma) + .02 * (window % 2 ? -1 : 1);
            double previous = controller.trial;
            auto result = controller.update(observed, 60, .175);
            if (std::abs(std::log(controller.trial / previous)) > .005001)
                throw std::runtime_error("Final correction exceeded half percent");
            if (result == ObservationMatcher::Matched)
            {
                matched = true;
                break;
            }
            if (result != ObservationMatcher::More)
                break;
        }
        if (!matched)
            throw std::runtime_error("Fine window matcher failed noisy convergence");
    }
    FineWindowMatcher impossible;
    bool bounded = false;
    for (int window = 0; window < 30; window++)
    {
        auto result = impossible.update(window % 2 ? 59. : 61., 60, .175);
        if (result != ObservationMatcher::More)
        {
            bounded = true;
            break;
        }
    }
    if (!bounded)
        throw std::runtime_error("Fine window matcher permitted an oscillation loop");
}

inline void continuousMatcherSelfTest()
{
    for (double gamma : {.35, 1., 2.2})
        for (double attenuation : {1., .8, .4})
            for (int delayFrames : {1, 4, 9})
                for (double timeConstantFrames : {.5, 2., 4.})
                {
                    ContinuousMatcher controller;
                    bool matched = false;
                    auto camera = [&](double signal)
                    { return 60 * std::pow(signal * attenuation / 100, gamma); };
                    // Delay-aware command gating: frame responses are lagged and exponential.
                    for (int iteration = 0; iteration < 50; iteration++)
                    {
                        double prior =
                            camera(controller.previousSignal > 0 ? controller.previousSignal : 100);
                        double observed = prior;
                        int horizon = delayFrames + int(std::ceil(7 * timeConstantFrames));
                        for (int frame = 0; frame < horizon; frame++)
                            if (frame >= delayFrames)
                                observed += (camera(controller.trial) - observed) *
                                            (1 - std::exp(-1. / timeConstantFrames));
                        auto result = controller.update(observed + .01 * (iteration % 2 ? 1 : -1),
                                                        60, .175, horizon / 30.);
                        if (result == ObservationMatcher::Matched)
                        {
                            matched = true;
                            break;
                        }
                        if (result != ObservationMatcher::More)
                            throw std::runtime_error(
                                "Continuous matcher failed delayed nonlinear response");
                    }
                    if (!matched || std::abs(camera(controller.trial) - 60) > .25)
                        throw std::runtime_error("Continuous matcher failed convergence");
                }
    ContinuousMatcher capped;
    bool plateau = false;
    for (int i = 0; i < 50; i++)
    {
        auto result = capped.update(std::min(capped.trial, 640.), 1000, .2);
        if (result == ObservationMatcher::Saturated)
        {
            plateau = true;
            break;
        }
        if (result != ObservationMatcher::More)
            break;
    }
    if (!plateau)
        throw std::runtime_error("Continuous matcher did not detect panel plateau");
}
