// SPDX-License-Identifier: GPL-3.0-only
#include "continuous_match.hpp"
#include "settling.hpp"
#include "flat_sweep.hpp"
#include "online_training.hpp"
#include <limits>
#include "reference_tracker.hpp"
#pragma once
struct MeasurementSession
{
    struct Query
    {
        std::string name, role;
        double hue, saturation, area, signal = 100;
        std::string asset;
        double probeBase = 0;
        std::string group, family;
        double level = 0;
        std::vector<double> state;
    };
    std::deque<Query> queue;
    Query current{};
    ReferenceTracker referenceTracker;
    std::string lastSampleName;
    std::vector<Query> closingReferences;
    OnlineTraining online;
    FlatSweep flatSweep;
    HsvZeroRegions zeroRegions;
    static bool isGrid(const Query& q){return q.family=="hsv-grid" || q.family=="hsv-grid-fixed-s";}
    bool skipGrid(const Query& q,double area) const {return isGrid(q) && zeroRegions.skip(q.hue,q.saturation,q.level,area,q.family=="hsv-grid");}
    FlatSweep brightnessSweep; // Retained across phases in this camera session.
    bool rawSeen = false, matchFlat = false;
    double rawError = 0;
    double rawCode = 0, rawSignal = 0, seedSlope = 20;
    double matchedArea = 0;
    bool preparing = false, active = false, pending = false;
    ULONGLONG started = 0, stimulus = 0;
    std::vector<double> readings;
    std::ofstream file;
    std::string path;
    ContinuousMatcher matcher;
    bool confirming = false, fineMode = false;
    FineWindowMatcher fineMatcher;
    double previousControlError = 0;
    int errorCrossings = 0;
    int totalQueries = 0;
    std::string progressPath;
    double target = 0, noise = 0;
    long exposure = 0;
    int exposureTries = 0;
    double maxError = 0;
    int completed = 0;
    double extraWait = 0;
    FrameSettling settling;
    bool averaging = false;
    ULONGLONG stableMs = 0;
    double baseline = 100;
    bool sameStimulus = false, lastStimulusSettled = false;
    long settledExposure = 0;
    bool plateauSearch = false, plateauVerify = false;
    double capLow = 0, capHigh = 0, capTarget = 0;
    int capIterations = 0;
    void prepare(const std::string &plan, const std::string &output = "",
                 const std::string &progressFile = "")
    {
        std::ifstream input(plan);
        if (!input)
        {
            throw std::runtime_error("Calibration plan missing");
        }
        std::string line;
        std::getline(input, line);
        queue.clear();
        closingReferences.clear();
        flatSweep = {};
        online.load(plan + ".online.bin");
        std::ifstream stateInput;if(online.histogram){stateInput.open(plan+".states.bin",std::ios::binary);if(!stateInput)throw std::runtime_error("Histogram training states missing");}
        while (std::getline(input, line))
        {
            if (line.empty())
                continue;
            std::istringstream s(line);
            std::string n, h, t, a, r, v, asset, pbase, group, family, level;
            std::getline(s, n, ',');
            std::getline(s, h, ',');
            std::getline(s, t, ',');
            std::getline(s, a, ',');
            std::getline(s, r, ',');
            std::getline(s, v, ',');
            std::getline(s, asset, ',');
            std::getline(s, pbase, ',');
            std::getline(s, group, ',');
            std::getline(s, family, ',');
            std::getline(s, level, ',');
            if (!group.empty() && group.back() == '\r')
                group.pop_back();
            if (!asset.empty() && asset.back() == '\r')
                asset.pop_back();
            if (!r.empty() && r.back() == '\r')
                r.pop_back();
            queue.push_back({n, r, std::stod(h), std::stod(t), std::stod(a),
                             v.empty() ? 100 : std::stod(v), asset,
                             pbase.empty() ? 0 : std::stod(pbase), group, family,
                             level.empty() ? 0 : std::stod(level)});
            if(online.enabled && r=="match") {
                queue.back().state.resize(online.dimensions);
                if(online.histogram) {
                    std::string coordinate;std::getline(s,coordinate,',');if(coordinate.empty())throw std::runtime_error("Missing histogram state index");
                    auto index=std::stoull(coordinate);stateInput.seekg(index*online.dimensions*sizeof(double));stateInput.read((char*)queue.back().state.data(),online.dimensions*sizeof(double));if(!stateInput)throw std::runtime_error("Truncated histogram training states");
                } else for(unsigned i=0;i<online.dimensions;i++){std::string coordinate;std::getline(s,coordinate,',');if(coordinate.empty())throw std::runtime_error("Missing online training coordinates");queue.back().state[i]=std::stod(coordinate);}
            }
        }
        if (queue.empty())
            throw std::runtime_error("Calibration plan contains no measurements");
        totalQueries = int(queue.size());
        char *progressEnvironment = nullptr;
        size_t progressLength = 0;
        if (_dupenv_s(&progressEnvironment, &progressLength, "OLED_CALIBRATION_PROGRESS") == 0 &&
            progressEnvironment)
        {
            progressPath = progressEnvironment;
            free(progressEnvironment);
        }
        progress("Camera sync", 0);
        started = GetTickCount64();
        path = output.empty() ? "hsv-observations-" + std::to_string(started) + ".csv" : output;
        if (!progressFile.empty())
            progressPath = progressFile;
        file.close();
        file.clear();
        completed = 0;
        maxError = 0;
        file.open(path);
        if (!file)
            throw std::runtime_error("Cannot write calibration observations");
        file << "host_ms,name,hue,saturation,area,role,signal_nits,camera_code,reference_code,"
                "standard_error,"
                "exposure_log2_s,stimulus_ms,probe_signal_nits,stable_ms,variation_codes,drift_"
                "codes\n";
        preparing = true;
        active = false;
        if (!preparation.valid) { referenceTracker = {}; preparation.begin(!roiReady); }
        log("Calibration measurements: " + path);
    }
    void progress(const std::string &phase, int done)
    {
        if (progressPath.empty())
            return;
        std::ofstream p(progressPath);
        p << done << ',' << totalQueries << ',' << phase << std::endl;
    }
    void presented(ULONGLONG now)
    {
        if (pending)
        {
            pending = false;
            stimulus = now;
        }
    }
    void display(double signal)
    {
        float probe = float(current.probeBase > 0 ? signal * current.probeBase / 250 : signal);
        sameStimulus =
            lastStimulusSettled && settledExposure == camera.exp &&
            params.mode == (current.asset.empty() ? 7 : 8) && params.color == 4 &&
            params.hue == float(current.hue) && params.saturation == float(current.saturation) &&
            params.area == float(current.area) && params.nits == float(signal) &&
            params.probeNits == probe && (current.asset.empty() || scenePath == current.asset);
        lastStimulusSettled = false;
        params.mode = current.asset.empty() ? 7 : 8;
        if (!current.asset.empty())
            loadScene(current.asset);
        params.color = 4;
        params.hue = float(current.hue);
        params.saturation = float(current.saturation);
        params.area = float(current.area);
        params.nits = float(signal);
        params.probeNits = float(current.probeBase > 0 ? signal * current.probeBase / 250 : signal);
        readings.clear();
        settling.reset();
        averaging = false;
        stableMs = 0;
        extraWait = 0;
        pending = true;
        stimulus = GetTickCount64();
        preparation.status = "Calibration " + current.name + " " + current.role + " area " +
                             std::to_string(int(current.area * 100)) + "%";
    }
    void finish(const std::string &message)
    {
        active = preparing = false;
        file.close();
        progress(message.rfind("PASS", 0) == 0 ? "Complete" : "Stopped",
                 totalQueries - int(queue.size()));
        log(message + "; " + path);
        preparation.status = message;
        params.mode = 4;
        params.area = .01f;
        params.nits = params.probeNits = 100;
    }
    void next()
    {
        while (!queue.empty() && queue.front().role != "reference" &&
               queue.front().role != "end_reference")
        {
            const auto first = queue.front();
            double window = 0;
            for (const auto &query : queue)
            {
                if (query.name != first.name)
                    break;
                if (query.role == "match")
                    window = query.area;
            }
            if (!flatSweep.skip(first.group, window) &&
                (isGrid(first) || !brightnessSweep.skipBrightness(first.family, first.level)) && !skipGrid(first,window))
                break;
            current = first;
            current.area = window;
            for (const auto& query : queue) {
                if (query.name != first.name) break;
                if (query.role == "match") {
                    current = query;
                    if (online.enabled) online.observe(query.state, 250);
                    break;
                }
            }
            // Inferred unity gain is a training label, not a camera measurement.
            save(NAN, NAN, "skipped_flat", 250);
            while (!queue.empty() && queue.front().name == first.name)
                queue.pop_front();
            log("Skipping flat region: " + first.name);
        }
        if (queue.empty())
        {
            finish("PASS Calibration acquisition; observed comparisons saved");
            return;
        }
        // All sweeps share the synchronized reference and fixed exposure.
        if (queue.front().role == "reference") {
            current = queue.front(); queue.pop_front();
            if (online.enabled) online.beginSweep();
            save(target, noise, "reference");
            next(); return;
        }
        if (queue.front().role == "end_reference" && queue.size() > 1) {
            closingReferences.push_back(queue.front()); queue.pop_front();
            next(); return;
        }
        const auto& nextQuery = queue.front();
        if ((nextQuery.role == "raw" || nextQuery.role == "predicted_adaptive" || nextQuery.role == "match") &&
            nextQuery.name != lastSampleName && referenceTracker.due(GetTickCount64())) {
            current = {"periodic-reference", "baseline", 0, 0, .01, 250, "", 100};
            confirming = fineMode = plateauSearch = plateauVerify = false;
            progress("Reference check", totalQueries - int(queue.size()));
            display(250);
            return;
        }
        current = queue.front();
        queue.pop_front();
        if(online.enabled && current.role=="reference")online.beginSweep();
        if(online.enabled && current.role=="match")current.signal=250*std::exp(online.predict(current.state));
        confirming = fineMode = false;
        fineMatcher = {};
        previousControlError = 0;
        errorCrossings = 0;
        plateauSearch = plateauVerify = false;
        matcher = {};
        if (current.role == "match") {
            matcher.slope = seedSlope;
            if (online.enabled) { rawSeen = false; matchFlat = false; }
        }
        matcher.trial = current.signal;
        if (current.role == "raw" || current.role == "predicted_adaptive" || current.role == "match")
            lastSampleName = current.name;
        if (current.role == "raw")
        {
            rawSeen = false;
            matchFlat = false;
            rawError = 0;
            matchedArea = 0;
            seedSlope = 20;
        }
        if (current.role == "match")
            matchedArea = current.area;
        progress(current.role == "reference" || current.role == "end_reference" ? "Reference"
                 : current.role == "match"                                      ? "Matching"
                                                                                : "Measuring",
                 totalQueries - int(queue.size()) - 1);
        display(current.signal);
        if (current.role == "match") save(NAN, noise, "match_seed");
    }
    void save(double y, double se, const std::string &role, double signal = NAN)
    {
        file << GetTickCount64() << ',' << current.name << ',' << current.hue << ','
             << current.saturation << ',' << current.area << ',' << role << ',' << (std::isfinite(signal) ? signal : params.nits)
             << ',' << y << ',' << target << ',' << se << ',' << camera.exp << ',' << stimulus
             << ',' << params.probeNits << ',' << stableMs << ',' << settling.variation << ','
             << settling.drift << std::endl;
        ++completed;
    }
    void updateReference(double value) {
        double change = referenceTracker.level > 0 ? 100 * (value / referenceTracker.level - 1) : 0;
        bool warning = referenceTracker.update(value, camera.arrival);
        target = value;
        preparation.target = value;
        log(std::string(warning ? "WARNING: Reference brightness changed by " : "Reference brightness changed by ") +
            std::to_string(change) + "%" + (warning ? "; possible camera exposure change. Reference updated; exposure setting unchanged." : "; reference updated."));
    }
    double accuracyTolerance() const
    {
        return std::max(4 * noise, .005 * std::abs(target));
    }
    void tick(ULONGLONG now)
    {
        if (!active && !preparing)
            return;
        if (preparing)
        {
            if (preparation.stage == CameraPreparation::Idle && preparation.target <= 0)
            {
                finish("Camera preparation failed: " + preparation.status);
                return;
            }
            if (preparation.stage == CameraPreparation::Idle && preparation.target > 0)
            {
                preparing = false;
                active = true;
                target = preparation.target;
                noise = preparation.noise;
                exposure = preparation.exposure;
                if (referenceTracker.level == 0) referenceTracker.update(target, now);
                lastSampleName.clear();
                next();
            }
            return;
        }
        if (!frameValid || pending)
        {
            if (!pending && now >= stimulus && now - stimulus > 12000)
                finish("Camera acquisition stopped: measurement region has no usable frames");
            return;
        }
        if (current.role == "response")
        {
            double y = (meanRGB[0] + meanRGB[1] + meanRGB[2]) / 3;
            save(y, 0, "response_trace");
            if (camera.arrival >= stimulus + 2000)
                next();
            return;
        }
        if (camera.arrival <
            stimulus +
                ULONGLONG(std::ceil((sameStimulus ? 0
                                                  : preparation.cameraDelayMs +
                                                        std::max(50., preparation.settlingMs)) +
                                    0 + extraWait)))
            return;
        double score = (meanRGB[0] + meanRGB[1] + meanRGB[2]) / 3;
        if (camera.exp != exposure && current.role != "reference")
        {
            finish("Calibration acquisition incomplete: exposure changed");
            return;
        }
        if (clipped > .001 && current.role != "reference")
        {
            finish("Calibration acquisition incomplete: camera clipping");
            return;
        }
        if ((current.role == "match" || current.role == "range") && !plateauSearch && !confirming &&
            !fineMode)
        {
            double error = (current.role == "range" ? 255. : target) - score;
            if (previousControlError * error < 0)
            {
                ++errorCrossings;
                matcher.integral = 0;
                matcher.stepLimit = std::max(.005, .25 * std::pow(.5, errorCrossings));
            }
            previousControlError = error;
            if (std::abs(error) <= accuracyTolerance())
            {
                fineMode = true;
                confirming = true;
                matcher.integral = 0;
                fineMatcher = {};
                fineMatcher.trial = params.nits;
                fineMatcher.step =
                    std::clamp(std::abs(error) / std::max(1., matcher.slope), .0002, .005);
                settling.reset();
                readings.clear();
                averaging = false;
                sameStimulus = false;
                stimulus = camera.arrival; // Start an explicit final observation window.
                progress("Final windows", totalQueries - int(queue.size()) - 1);
                return;
            }
            save(score, noise * std::sqrt(3.), "control_frame");
            auto result = matcher.update(score, current.role == "range" ? 255. : target,
                                         accuracyTolerance(),
                                         double(camera.arrival - stimulus) / 1000);
            if (result == ObservationMatcher::More)
            {
                display(matcher.trial);
                return;
            }
            if (result == ObservationMatcher::Saturated)
            {
                plateauSearch = true;
                capIterations = 0;
                capHigh = matcher.bestSignal;
                capLow =
                    matcher.previousBestSignal > 0 ? matcher.previousBestSignal : capHigh / 1.5;
                capTarget = matcher.bestObserved;
                save(score, noise * std::sqrt(3.), "plateau_detected");
                display(std::sqrt(capLow * capHigh));
                return;
            }
            if (result != ObservationMatcher::Matched)
            {
                save(score, noise * std::sqrt(3.),
                     result == ObservationMatcher::Unreachable ? "pq_domain_limit"
                                                               : "search_limit");
                next();
                return;
            }
            confirming = true;
            settling.reset();
            readings.clear();
            averaging = false;
            progress(current.name.rfind("neutral-fine-", 0) == 0 ? "Gray confirm" : "Confirming",
                     totalQueries - int(queue.size()) - 1);
        }
        if (plateauSearch && !plateauVerify)
        {
            save(score, noise * std::sqrt(3.), "plateau_trial");
            double tol = std::max(.175, 6 * noise);
            if (score >= capTarget - tol)
                capHigh = params.nits;
            else
                capLow = params.nits;
            if (++capIterations >= 8 || capHigh / capLow <= 1.02)
            {
                plateauVerify = true;
                display(capHigh);
            }
            else
                display(std::sqrt(capLow * capHigh));
            return;
        }
        if (sameStimulus && extraWait == 0)
            averaging = true;
        if (!averaging)
        {
            if (camera.arrival > stimulus + 5000)
            {
                finish("Acquisition rejected: camera response did not stabilize");
                return;
            }
            if (settling.add(score, noise))
            {
                averaging = true;
                stableMs = camera.arrival - stimulus;
                readings.assign(settling.values.begin(), settling.values.end());
            }
            else
                return;
        }
        else
            readings.push_back(score);
        lastStimulusSettled = true;
        settledExposure = camera.exp;
        // Use the latest settled frame. History serves only to detect settling.
        double y = score;
        double se = settling.values.size() >= 3 ? settling.variation / std::sqrt(2.) : noise;
        if (camera.exp != exposure || clipped > .001)
        {
            finish("Calibration acquisition incomplete: clipping/exposure changed");
            return;
        }
        if (current.role == "match" || current.role == "range")
        {
            if (plateauSearch)
            {
                save(y, se, "panel_plateau");
                log("Measured plateau at input signal " + std::to_string(params.nits) + " for " +
                    current.name);
                next();
                return;
            }
            double tolerance =
                accuracyTolerance();
            auto result =
                fineMode ? fineMatcher.update(y, current.role == "range" ? 255. : target, tolerance)
                         : (std::abs(y - (current.role == "range" ? 255. : target)) <= tolerance
                                ? ObservationMatcher::Matched
                                : ObservationMatcher::More);
            if (result != ObservationMatcher::Matched)
            {
                save(y, se, "final_window");
                if (!fineMode)
                {
                    fineMode = true;
                    fineMatcher = {};
                    fineMatcher.trial = params.nits;
                    fineMatcher.step =
                        std::clamp(std::abs(y - target) / std::max(1., matcher.slope), .0002, .005);
                    matcher.integral = 0;
                    result =
                        fineMatcher.update(y, current.role == "range" ? 255. : target, tolerance);
                }
                if (result == ObservationMatcher::More)
                {
                    display(fineMatcher.trial);
                    sameStimulus = false;
                    progress("Final windows", totalQueries - int(queue.size()) - 1);
                }
                else
                {
                    save(y, se,
                         result == ObservationMatcher::Saturated ? "panel_plateau"
                                                                 : "search_limit");
                    log(result == ObservationMatcher::Saturated
                            ? "Final windows reached panel plateau: " + current.name
                            : "Final windows did not converge: " + current.name);
                    next();
                }
                return;
            }
            save(y, se, "matched");
            if(online.enabled)online.observe(current.state,online.histogram && FlatSweep::smallCorrection(params.nits,250)?250:params.nits);
            matchFlat = FlatSweep::smallCorrection(params.nits, 250);
            matchedArea = current.area;
            save(y, se, "calibrated");
            bool flat =
                matchFlat && (!rawSeen || rawError <= std::max(accuracyTolerance(), matcher.slope * std::log(1.01)));
            flatSweep.observe(current.group, current.area, flat);
            if(!isGrid(current))brightnessSweep.observeBrightness(current.family, current.level, current.area, flat);
            if(flat && isGrid(current))zeroRegions.observe(current.hue,current.saturation,current.level,current.area);
            maxError = std::max(maxError, std::abs(y - target));
            next();
            return;
        }
        if (current.role == "baseline") {
            save(y, se, "baseline");
            updateReference(y);
            next(); return;
        }
        if (current.role == "end_reference") {
            save(y, se, "end_reference_check");
            updateReference(y);
            save(y, se, "end_reference");
            for (const auto& reference : closingReferences) {
                current = reference;
                save(y, se, "end_reference");
            }
            closingReferences.clear();
            next(); return;
        }
        save(y, se, current.role);
        if (current.role == "raw")
        {
            rawSeen = true;
            rawError = std::abs(y - target);
            rawCode = y;
            rawSignal = params.nits;
        }
        if (current.role == "predicted_adaptive" && rawSeen && rawSignal > 0)
        {
            double delta = std::log(params.nits / rawSignal);
            if (std::abs(delta) > .015)
            {
                double measured = (y - rawCode) / delta;
                if (std::isfinite(measured) && measured > 1)
                    seedSlope = std::clamp(measured, 1., 500.);
            }
        }

        next();
    }
} measurementSession;
