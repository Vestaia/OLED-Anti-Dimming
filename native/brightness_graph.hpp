// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <deque>
struct BrightnessGraph
{
    struct Point
    {
        ULONGLONG time;
        double code;
        int phase;
    };
    std::deque<Point> points;
    std::deque<double> average;
    RECT lastROI{};
    long lastExposure = 999;
    void sample(ULONGLONG now)
    {
        if (!frameValid)
            return;
        if (lastExposure != camera.exp || !EqualRect(&lastROI, &roi))
        {
            points.clear();
            average.clear();
            lastROI = roi;
            lastExposure = camera.exp;
        }
        average.push_back((meanRGB[0] + meanRGB[1] + meanRGB[2]) / 3);
        if (average.size() > 1)
            average.pop_front();
        double code = std::accumulate(average.begin(), average.end(), 0.) / average.size();
        int phase = 0;

        points.push_back({now, code, phase});
        while (!points.empty() && now - points.front().time > 8000)
            points.pop_front();
    }
    void draw(HDC dc, ULONGLONG now)
    {
        RECT background{0, 465, 640, 680};
        auto brush = CreateSolidBrush(RGB(20, 20, 20));
        FillRect(dc, &background, brush);
        DeleteObject(brush);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(230, 230, 230));
        RECT label{12, 470, 630, 493};
        DrawTextW(dc, L"Center brightness: camera codes | last 8 s | latest frame", -1, &label,
                  DT_LEFT);
        RECT plot{58, 503, 625, 636};
        double low = 255, high = 0;
        for (auto p : points)
        {
            low = std::min(low, p.code);
            high = std::max(high, p.code);
        }
        if (preparation.valid)
        {
            low = std::min(low, preparation.target);
            high = std::max(high, preparation.target);
        }
        if (points.empty())
        {
            RECT r{70, 535, 620, 565};
            DrawTextW(dc, L"Select the center ROI to see measurements", -1, &r, DT_LEFT);
            return;
        }
        double padding = std::max(3., (high - low) * .15);
        low = std::max(0., low - padding);
        high = std::min(255., high + padding);
        if (high - low < 1)
            high = low + 1;
        auto y = [&](double code)
        { return plot.bottom - int((code - low) / (high - low) * (plot.bottom - plot.top)); };
        auto grid = CreatePen(PS_SOLID, 1, RGB(65, 65, 65));
        auto old = SelectObject(dc, grid);
        for (int i = 0; i < 3; ++i)
        {
            int yy = plot.top + (plot.bottom - plot.top) * i / 2;
            MoveToEx(dc, plot.left, yy, nullptr);
            LineTo(dc, plot.right, yy);
            wchar_t text[32];
            swprintf_s(text, L"%.1f", high - (high - low) * i / 2);
            TextOutW(dc, 5, yy - 7, text, int(wcslen(text)));
        }
        SelectObject(dc, old);
        DeleteObject(grid);
        if (preparation.valid)
        {
            auto pen = CreatePen(PS_DOT, 1, RGB(170, 170, 170));
            old = SelectObject(dc, pen);
            MoveToEx(dc, plot.left, y(preparation.target), nullptr);
            LineTo(dc, plot.right, y(preparation.target));
            SelectObject(dc, old);
            DeleteObject(pen);
        }
        for (size_t i = 1; i < points.size(); ++i)
        {
            auto a = points[i - 1], b = points[i];
            if (a.phase != b.phase || b.time - a.time > 250)
                continue;
            COLORREF color = b.phase == 2   ? RGB(75, 225, 135)
                             : b.phase == 1 ? RGB(255, 165, 65)
                                            : RGB(100, 185, 255);
            auto pen = CreatePen(PS_SOLID, 2, color);
            old = SelectObject(dc, pen);
            int x1 = plot.right - int(double(now - a.time) / 8000 * (plot.right - plot.left)),
                x2 = plot.right - int(double(now - b.time) / 8000 * (plot.right - plot.left));
            MoveToEx(dc, x1, y(a.code), nullptr);
            LineTo(dc, x2, y(b.code));
            SelectObject(dc, old);
            DeleteObject(pen);
        }
        RECT footer{58, 642, 630, 677};
        DrawTextW(dc,
                  L"-8 s                                       now\nOrange: uncalibrated   Green: "
                  L"calibrated   Dotted: reference",
                  -1, &footer, DT_LEFT);
    }
} brightnessGraph;
