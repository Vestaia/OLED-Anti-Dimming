#include "continuous_match.hpp"
#include "settling.hpp"
#include "flat_sweep.hpp"
#pragma once
struct HSVCalibration {
    struct Query {
        std::string name, role;
        double hue, saturation, area, signal = 100;
        std::string asset;
        double probeBase = 0;
        std::string group,family;
        double level=0;
    };
    std::deque<Query> queue;
    Query current{};
    FlatSweep flatSweep;
    FlatSweep brightnessSweep; // Retained across phases in this camera session.
    bool rawSeen=false,matchFlat=false;
    double rawError=0;
    double rawCode=0,rawSignal=0,seedSlope=20;
    double matchedArea=0;
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
    void prepare(const std::string &plan, const std::string &output = "", const std::string &progressFile = "") {
        std::ifstream input(plan);
        if (!input) {
            log("HSV plan missing");
            return;
        }
        std::string line;
        std::getline(input, line);
        queue.clear();
        flatSweep={};
        while (std::getline(input, line)) {
            if (line.empty())
                continue;
            std::istringstream s(line);
            std::string n, h, t, a, r, v, asset, pbase,group,family,level;
            std::getline(s, n, ',');
            std::getline(s, h, ',');
            std::getline(s, t, ',');
            std::getline(s, a, ',');
            std::getline(s, r, ',');
            std::getline(s, v, ',');
            std::getline(s, asset, ',');
            std::getline(s, pbase, ',');
            std::getline(s,group,',');std::getline(s,family,',');std::getline(s,level);if(!group.empty()&&group.back()=='\r')group.pop_back();
            if (!asset.empty() && asset.back() == '\r')
                asset.pop_back();
            if (!r.empty() && r.back() == '\r')
                r.pop_back();
            queue.push_back({n, r, std::stod(h), std::stod(t), std::stod(a), v.empty() ? 100 : std::stod(v),
                             asset, pbase.empty() ? 0 : std::stod(pbase),group,family,level.empty()?0:std::stod(level)});
        }
        totalQueries = int(queue.size());
        char *progressEnvironment = nullptr;
        size_t progressLength = 0;
        if (_dupenv_s(&progressEnvironment, &progressLength, "OLED_CALIBRATION_PROGRESS") == 0 &&
            progressEnvironment) {
            progressPath = progressEnvironment;
            free(progressEnvironment);
        }
        progress("Camera sync", 0);
        started = GetTickCount64();
        path = output.empty() ? "hsv-observations-" + std::to_string(started) + ".csv" : output;
        if(!progressFile.empty())progressPath=progressFile;
        file.close();file.clear();completed=0;maxError=0;
        file.open(path);
        if(!file)throw std::runtime_error("Cannot write calibration observations");
        file << "host_ms,name,hue,saturation,area,role,signal_nits,camera_code,reference_code,standard_error,"
                "exposure_log2_s,stimulus_ms,probe_signal_nits,stable_ms,variation_codes,drift_codes\n";
        preparing = true;active=false;white.target=0;white.timingOnly = true;white.begin(!roiReady);
        log("Short sparse HSV experiment; no strict one-minute cutoff; " + path);
    }
    void progress(const std::string &phase, int done) {
        if (progressPath.empty())
            return;
        std::ofstream p(progressPath);
        p << done << ',' << totalQueries << ',' << phase << std::endl;
    }
    void presented(ULONGLONG now) {
        if (pending) {
            pending = false;
            stimulus = now;
        }
    }
    void display(double signal) {
        float probe = float(current.probeBase > 0 ? signal * current.probeBase / 250 : signal);
        sameStimulus = lastStimulusSettled && settledExposure == camera.exp &&
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
        white.status = "HSV " + current.name + " " + current.role + " area " +
                       std::to_string(int(current.area * 100)) + "%";
    }
    void finish(const std::string &message) {
        active = preparing = false;
        file.close();
        progress(message.rfind("PASS", 0) == 0 ? "Complete" : "Stopped", totalQueries - int(queue.size()));
        log(message + "; " + path);
        white.status = message;
        params.mode = 4;
        params.area = .01f;
        params.nits = params.probeNits = 100;
    }
    void next() {
        while(!queue.empty()&&queue.front().role!="reference"&&queue.front().role!="end_reference"){
            const auto first=queue.front();
            double window=0;
            for(const auto& query:queue){if(query.name!=first.name)break;if(query.role=="match")window=query.area;}
            if(!flatSweep.skip(first.group,window)&&!brightnessSweep.skipBrightness(first.family,first.level))break;
            current=first;current.area=window;
            save(NAN,NAN,"skipped_flat");
            while(!queue.empty()&&queue.front().name==first.name)queue.pop_front();
            log("Skipping flat region: "+first.name);
        }
        if (queue.empty()) {
            finish("PASS HSV acquisition; observed comparisons saved");
            return;
        }
        current = queue.front();
        queue.pop_front();
        confirming = fineMode = false;
        fineMatcher = {};
        previousControlError = 0;
        errorCrossings = 0;
        plateauSearch = plateauVerify = false;
        matcher = {};
        if(current.role=="match")matcher.slope=seedSlope;
        matcher.trial = current.signal;
        if(current.role=="raw"){rawSeen=false;matchFlat=false;rawError=0;matchedArea=0;seedSlope=20;}
        if(current.role=="match")matchedArea=current.area;
        if (current.role == "reference") {
            rawSeen=matchFlat=false;rawError=0;matchedArea=0;
            exposureTries = 0;
            target = 0;
            baseline = current.signal;
            noise = 0;
        }
        progress(current.role == "reference" || current.role == "end_reference" ? "Reference"
                 : current.role == "match"                                      ? "Matching"
                                                                                : "Measuring",
                 totalQueries - int(queue.size()) - 1);
        display(current.signal);
    }
    void save(double y, double se, const std::string &role) {
        file << GetTickCount64() << ',' << current.name << ',' << current.hue << ',' << current.saturation
             << ',' << current.area << ',' << role << ',' << params.nits << ',' << y << ',' << target << ','
             << se << ',' << camera.exp << ',' << stimulus << ',' << params.probeNits << ',' << stableMs
             << ',' << settling.variation << ',' << settling.drift << std::endl;
        ++completed;
    }
    double accuracyTolerance() const { return current.name.rfind("neutral-fine-", 0) == 0 ? .175 : .35; }
    void tick(ULONGLONG now) {
        if (!active && !preparing)
            return;
        if (preparing) {
            if (white.stage == WhiteCalibration::Idle && white.target <= 0) {
                finish("Camera preparation failed: " + white.status);
                return;
            }
            if (white.stage == WhiteCalibration::Idle && white.target > 0) {
                preparing = false;
                active = true;
                next();
            }
            return;
        }
        if (!frameValid || pending) {
            if(!pending&&now>=stimulus&&now-stimulus>12000)finish("Camera acquisition stopped: measurement region has no usable frames");
            return;
        }
        if (current.role == "response") {
            double y = (meanRGB[0] + meanRGB[1] + meanRGB[2]) / 3;
            save(y, 0, "response_trace");
            if (camera.arrival >= stimulus + 2000)
                next();
            return;
        }
        if (camera.arrival <
            stimulus + ULONGLONG(std::ceil(
                           (sameStimulus ? 0 : white.cameraDelayMs + std::max(50., white.settlingMs)) +
                           0 + extraWait)))
            return;
        double score = (meanRGB[0] + meanRGB[1] + meanRGB[2]) / 3;
        if (camera.exp != exposure && current.role != "reference") {
            finish("HSV acquisition incomplete: exposure changed");
            return;
        }
        if (clipped > .001 && current.role != "reference") {
            finish("HSV acquisition incomplete: camera clipping");
            return;
        }
        if ((current.role == "match" || current.role == "range") && !plateauSearch && !confirming &&
            !fineMode) {
            double error = (current.role == "range" ? 255. : target) - score;
            if (previousControlError * error < 0) {
                ++errorCrossings;
                matcher.integral = 0;
                matcher.gainScale = std::max(.2, 1. / (1 + errorCrossings));
                matcher.stepLimit = std::max(.005, .25 * std::pow(.5, errorCrossings));
            }
            previousControlError = error;
            if ((std::abs(error) / std::max(1., matcher.slope) <= .01 &&
                 std::abs(error) <= std::max(1., 6 * noise)) ||
                std::abs(error) <= accuracyTolerance()) {
                fineMode = true;
                confirming = true;
                matcher.integral = 0;
                fineMatcher = {};
                fineMatcher.trial = params.nits;
                fineMatcher.step = std::clamp(std::abs(error) / std::max(1., matcher.slope), .0002, .005);
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
                                         std::max(accuracyTolerance(), 6 * noise),
                                         double(camera.arrival - stimulus) / 1000);
            if (result == ObservationMatcher::More) {
                display(matcher.trial);
                return;
            }
            if (result == ObservationMatcher::Saturated) {
                plateauSearch = true;
                capIterations = 0;
                capHigh = matcher.bestSignal;
                capLow = matcher.previousBestSignal > 0 ? matcher.previousBestSignal : capHigh / 1.5;
                capTarget = matcher.bestObserved;
                save(score, noise * std::sqrt(3.), "plateau_detected");
                display(std::sqrt(capLow * capHigh));
                return;
            }
            if (result != ObservationMatcher::Matched) {
                save(score, noise * std::sqrt(3.),
                     result == ObservationMatcher::Unreachable ? "pq_domain_limit" : "search_limit");
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
        if (plateauSearch && !plateauVerify) {
            save(score, noise * std::sqrt(3.), "plateau_trial");
            double tol = std::max(.175, 6 * noise);
            if (score >= capTarget - tol)
                capHigh = params.nits;
            else
                capLow = params.nits;
            if (++capIterations >= 8 || capHigh / capLow <= 1.02) {
                plateauVerify = true;
                display(capHigh);
            } else
                display(std::sqrt(capLow * capHigh));
            return;
        }
        if (sameStimulus && extraWait == 0)
            averaging = true;
        if (!averaging) {
            if (camera.arrival > stimulus + 5000) {
                finish("Acquisition rejected: camera response did not stabilize");
                return;
            }
            if (settling.add(score, noise)) {
                averaging = true;
                stableMs = camera.arrival - stimulus;
                readings.assign(settling.values.begin(), settling.values.end());
            } else
                return;
        } else
            readings.push_back(score);
        lastStimulusSettled = true;
        settledExposure = camera.exp;
        // Use the latest settled frame. History serves only to detect settling.
        double y=score;
        double se=settling.values.size()>=3?settling.variation/std::sqrt(2.):noise;
        if (current.role == "reference") {
            long e = camera.exp;
            if (y < 25)
                e += camera.estep;
            if (y > 90 || p99 > 180 || clipped > .001)
                e -= camera.estep;
            e = std::min(e, -5L);
            if (camera.exposureApi && e != camera.exp && e >= camera.emin && e <= camera.emax && exposureTries++ < 5) {
                camera.exposure(e);
                display(baseline);
                extraWait = 400;
                return;
            }
            if (y < 12 || p99 > 240 || clipped > .001) {
                save(y, se, "unusable_reference");
                finish("HSV acquisition incomplete: unusable camera range");
                return;
            }
            target = y;
            noise = se;
            exposure = camera.exp;
            save(y, se, "reference");
            next();
            return;
        }
        if (camera.exp != exposure || clipped > .001) {
            finish("HSV acquisition incomplete: clipping/exposure changed");
            return;
        }
        if (current.role == "match" || current.role == "range") {
            if (plateauSearch) {
                save(y, se, "panel_plateau");
                log("Measured plateau at input signal " + std::to_string(params.nits) + " for " +
                    current.name);
                next();
                return;
            }
            double tolerance = std::max(accuracyTolerance(), 3 * std::sqrt(noise * noise + se * se));
            auto result = fineMode ? fineMatcher.update(y, current.role == "range" ? 255. : target, tolerance)
                                   : (std::abs(y - (current.role == "range" ? 255. : target)) <= tolerance
                                          ? ObservationMatcher::Matched
                                          : ObservationMatcher::More);
            if (result != ObservationMatcher::Matched) {
                save(y, se, "final_window");
                if (!fineMode) {
                    fineMode = true;
                    fineMatcher = {};
                    fineMatcher.trial = params.nits;
                    fineMatcher.step =
                        std::clamp(std::abs(y - target) / std::max(1., matcher.slope), .0002, .005);
                    matcher.integral = 0;
                    result = fineMatcher.update(y, current.role == "range" ? 255. : target, tolerance);
                }
                if (result == ObservationMatcher::More) {
                    display(fineMatcher.trial);
                    sameStimulus = false;
                    progress("Final windows", totalQueries - int(queue.size()) - 1);
                } else {
                    save(y, se, result == ObservationMatcher::Saturated ? "panel_plateau" : "search_limit");
                    log(result == ObservationMatcher::Saturated
                            ? "Final windows reached panel plateau: " + current.name
                            : "Final windows did not converge: " + current.name);
                    next();
                }
                return;
            }
            save(y, se, "matched");
            matchFlat=FlatSweep::smallCorrection(params.nits,250);
            matchedArea=current.area;
            save(y, se, "calibrated");
            bool flat=rawSeen&&matchFlat&&rawError<=std::max({accuracyTolerance(),3*std::sqrt(noise*noise+se*se),matcher.slope*std::log(1.01)});
            flatSweep.observe(current.group,current.area,flat);
            brightnessSweep.observeBrightness(current.family,current.level,current.area,flat);
            maxError = std::max(maxError, std::abs(y - target));
            next();
            return;
        }
        save(y, se, current.role);
        if(current.role=="raw"){rawSeen=true;rawError=std::abs(y-target);rawCode=y;rawSignal=params.nits;}
        if(current.role=="predicted_adaptive"&&rawSeen&&rawSignal>0){
            double delta=std::log(params.nits/rawSignal);
            if(std::abs(delta)>.015){double measured=(y-rawCode)/delta;if(std::isfinite(measured)&&measured>1)seedSlope=std::clamp(measured,1.,500.);}
        }
        if(current.role=="end_reference"&&matchedArea>0){
            bool flat=rawSeen&&matchFlat&&std::abs(y-target)<=.5&&rawError<=std::max({accuracyTolerance(),3*std::sqrt(noise*noise+se*se),matcher.slope*std::log(1.01)});
            flatSweep.observe(current.group,matchedArea,flat);
            brightnessSweep.observeBrightness(current.family,current.level,matchedArea,flat);
        }
        if (current.role == "end_reference" &&
            std::abs(y - target) > std::max(2., 3 * std::sqrt(noise * noise + se * se)))
            log("HSV reference drift for " + current.name + "; mark curve tentative");
        next();
    }
} hsvExperiment;
