// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include "match.hpp"
#include "timing.hpp"
#include <deque>
#include <numeric>

// Camera-only preparation: locate the reference, lock exposure, measure response.
struct CameraPreparation
{
    enum Stage
    {
        Idle,
        LocateDark,
        LocateBright,
        Exposure,
        Reference,
        Sync
    } stage = Idle;
    std::vector<double> readings;
    std::vector<unsigned char> dark;
    ULONGLONG started = 0, stimulus = 0;
    long exposure = 0;
    double baseline = 100, target = 0, noise = 0;
    int exposureAttempts = 0;
    bool valid = false, pendingPresent = false;
    std::string status = "Preparing camera";
    double cameraDelayMs = 250, settlingMs = 50, cameraFramePeriodMs = 33.333;
    int syncIndex = 0;
    std::vector<std::pair<double, bool>> transitions;
    std::vector<std::pair<double, double>> syncFrames;
    void presented(ULONGLONG now)
    {
        if (!pendingPresent)
            return;
        pendingPresent = false;
        stimulus = now;
        if (stage == Sync)
            transitions.push_back({double(now), params.nits > 50});
    }
    void set(double area, double signal)
    {
        params.mode = 4;
        params.color = 3;
        params.area = float(area);
        params.nits = params.probeNits = float(signal);
        stimulus = GetTickCount64();
        pendingPresent = true;
        readings.clear();
    }
    void stop(const std::string &reason)
    {
        log(reason);
        status = reason;
        stage = Idle;
        valid = false;
        set(.01, baseline);
    }
    void begin(bool locate = false)
    {
        if (!camera.fixedExposure() || !camera.fixedWhiteBalance())
        {
            stop("Calibration requires locked exposure and white balance");
            return;
        }
        if (!roiReady && !locate)
        {
            stop("Position the camera over the center reference patch");
            return;
        }
        started = GetTickCount64();
        valid = false;
        target = 0;
        exposureAttempts = 0;
        baseline = 100;
        camera.exposure(-5L);
        set(.01, locate ? 0 : baseline);
        stage = locate ? LocateDark : Exposure;
        status = "Preparing camera";
    }
    bool locate()
    {
        int w = camera.bmi.bmiHeader.biWidth, h = std::abs(camera.bmi.bmiHeader.biHeight),
            stride = (w * 3 + 3) & ~3;
        if (dark.size() != camera.bytes.size())
            return false;
        // Locate the changed patch, not the brightest object in the room.
        std::vector<double> difference(w * h);
        double maxDiff = 0;
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int yy = camera.bmi.bmiHeader.biHeight > 0 ? h - 1 - y : y;
                int i = yy * stride + x * 3;
                double d = 0;
                for (int c = 0; c < 3; ++c)
                    d += double(camera.bytes[i + c]) - dark[i + c];
                d /= 3;
                difference[y * w + x] = d;
                maxDiff = std::max(maxDiff, d);
            }
        if (maxDiff < 12)
            return false;
        std::vector<unsigned char> seen(w * h);
        std::vector<int> largest;
        for (int i = 0; i < w * h; ++i)
        {
            if (seen[i] || difference[i] < maxDiff * .55)
                continue;
            std::vector<int> component{i};
            seen[i] = 1;
            for (size_t j = 0; j < component.size(); ++j)
            {
                int p = component[j], x = p % w, y = p / w;
                for (int n : {x > 0 ? p - 1 : -1, x < w - 1 ? p + 1 : -1, y > 0 ? p - w : -1,
                              y < h - 1 ? p + w : -1})
                    if (n >= 0 && !seen[n] && difference[n] >= maxDiff * .55)
                    {
                        seen[n] = 1;
                        component.push_back(n);
                    }
            }
            if (component.size() > largest.size())
                largest = std::move(component);
        }
        if (largest.size() < 25)
            return false;
        double cx = 0, cy = 0;
        for (int p : largest)
        {
            cx += p % w;
            cy += p / w;
        }
        cx /= largest.size();
        cy /= largest.size();
        int x = int(cx), y = int(cy);
        // Grow a broad rectangle inside the detected changed patch rather than sampling a few
        // pixels.
        int radius = 0;
        for (int r = 2; r < std::min(w, h) / 2; ++r)
        {
            bool inside = true;
            for (int dy = -r; dy <= r && inside; ++dy)
                for (int dx = -r; dx <= r; ++dx)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || xx >= w || yy < 0 || yy >= h ||
                        difference[yy * w + xx] < maxDiff * .25)
                    {
                        inside = false;
                        break;
                    }
                }
            if (!inside)
                break;
            radius = r;
        }
        if (radius < 8)
            return false;
        radius = std::max(8, int(radius * .8));
        roi = {LONG((x - radius) * 640 / w), LONG((y - radius) * 360 / h),
               LONG((x + radius + 1) * 640 / w), LONG((y + radius + 1) * 360 / h)};
        roiReady = true;
        log("Spatial averaging over approximately " +
            std::to_string((2 * radius + 1) * (2 * radius + 1)) + " camera pixels inside probe");
        log("Located changed center patch ROI=" + std::to_string(roi.left) + "," +
            std::to_string(roi.top) + "," + std::to_string(roi.right) + "," +
            std::to_string(roi.bottom));
        return true;
    }
    void tick(ULONGLONG now)
    {
        if (stage == Idle)
            return;
        // A persistent-session command may initialize timestamps after the loop's
        // cached `now`. Never subtract a future timestamp from an unsigned clock.
        if (now >= started && now - started > 540000)
        {
            stop("Calibration incomplete: time ceiling reached");
            return;
        }
        if (!pendingPresent && now >= stimulus && now - stimulus > 12000 && readings.empty() &&
            stage != LocateDark && stage != LocateBright)
        {
            stop("Calibration stopped: no fresh usable camera samples");
            return;
        }
        if (!frameValid && stage != LocateDark && stage != LocateBright)
            return;
        if (stage == Sync)
        {
            double score = (meanRGB[0] + meanRGB[1] + meanRGB[2]) / 3;
            syncFrames.push_back({double(camera.arrival), score});
            static const bool bits[]{false, true, false, true, true,  false,
                                     false, true, false, true, false, true};
            if (!pendingPresent && now - stimulus >= 400 && syncIndex < 11)
            {
                ++syncIndex;
                set(.01, bits[syncIndex] ? 100 : 20);
                return;
            }
            if (syncIndex == 11 && now - stimulus >= 1000)
            {
                try
                {
                    auto d = estimateDelay(transitions, syncFrames);
                    cameraDelayMs = d.milliseconds;
                    auto response = estimateResponse(transitions, syncFrames, cameraDelayMs);
                    settlingMs = response.settlingMs;
                    cameraFramePeriodMs = response.framePeriodMs;
                    std::ofstream timing("camera-timing.json");
                    timing << "{\"effective_delay_ms\":" << cameraDelayMs
                           << ",\"observed_onset_median_ms\":" << response.onsetMs
                           << ",\"observed_stable_p90_ms\":" << response.stableMs
                           << ",\"received_frame_period_ms\":" << response.framePeriodMs
                           << ",\"settling_after_delay_ms\":" << settlingMs
                           << ",\"estimated_single_frame_measurement_ms\":"
                           << cameraDelayMs + settlingMs + response.framePeriodMs
                           << ",\"stable_edges\":" << response.transitions << "}";
                    log("Response stabilization p90=" + std::to_string(response.stableMs) +
                        " ms; frame interval=" + std::to_string(response.framePeriodMs) +
                        " ms; settling after delay=" + std::to_string(settlingMs) + " ms");
                    log("Timing pattern delay=" + std::to_string(cameraDelayMs) +
                        " ms contrast=" + std::to_string(d.contrast) +
                        " camera codes; adaptive settle >=50 ms; measurement=1 fresh frame");
                }
                catch (const std::exception &e)
                {
                    stop(e.what());
                    return;
                }
                stage = Reference;
                set(.01, baseline);
                status = "Synchronized reference: single settled frame";
            }
            return;
        }
        ULONGLONG settling = (stage == LocateDark || stage == LocateBright || stage == Exposure)
                                 ? 500
                                 : ULONGLONG(std::ceil(cameraDelayMs + settlingMs));
        if (pendingPresent || camera.arrival < stimulus + settling)
            return;
        if (stage == LocateDark)
        {
            dark = camera.bytes;
            set(.01, baseline);
            stage = LocateBright;
            return;
        }
        if (stage == LocateBright)
        {
            if (!locate())
            {
                stop(
                    "Could not locate center patch: reposition the camera and restart calibration");
                return;
            }
            set(.01, baseline);
            stage = Exposure;
            return;
        }
        double score = (meanRGB[0] + meanRGB[1] + meanRGB[2]) / 3;
        if (stage != Exposure && (camera.exp != exposure || !camera.fixedExposure()))
        {
            stop("Exposure changed during sweep; reference invalid");
            return;
        }
        if (stage != Exposure && clipped > .001)
        {
            stop("Camera clipping invalidates matching; shorten exposure before next sweep");
            return;
        }
        // One fresh frame after the measured response guard; no averaging wait.
        double y = score, se = 0;
        if (stage == Exposure)
        {
            long e = camera.exp;
            if (y < 25)
                e += camera.estep;
            if (y > 90 || p99 > 150 || clipped > .001)
                e -= camera.estep;
            e = std::min(e, -5L);
            if (camera.exposureApi && e != camera.exp && e >= camera.emin && e <= camera.emax &&
                exposureAttempts++ < 10)
            {
                camera.exposure(e);
                set(.01, baseline);
                return;
            }
            if (y < 15 || p99 > 170 || clipped > .001)
            {
                stop("No usable reference exposure: adjust camera framing or lighting");
                return;
            }
            exposure = camera.exp;
            stage = Sync;
            syncIndex = 0;
            transitions.clear();
            syncFrames.clear();
            set(.01, 20);
            status = "Measuring camera delay with binary brightness pattern";
            return;
        }
        if (stage == Reference)
        {
            target = y;
            noise = se;
            valid = true;
            stage = Idle;
            status = "Camera ready";
            log("Camera preparation complete");
        }
    }
} preparation;
