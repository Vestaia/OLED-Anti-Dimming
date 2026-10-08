"""Experimental HSV/area signal correction; unmeasured colors remain tentative."""
import bisect
import colorsys
import math


class HSVModel:
    def __init__(self, data):
        self.anchors = data['anchors']

    def signal(self, hue, saturation, area):
        if not 0 <= saturation <= 1 or not .01 <= area <= 1:
            raise ValueError('Supported saturation 0..1 and window area .01..1')
        rgb = colorsys.hsv_to_rgb(hue % 1, saturation, 1)
        neighbors = []
        for anchor in self.anchors:
            knots = anchor['knots']
            if area > knots[-1][0]:
                continue
            i = bisect.bisect_left([a for a, _ in knots], area)
            if i == 0:
                logsignal = math.log(knots[0][1])
            else:
                a, b = knots[i-1], knots[i]
                t = (area-a[0])/(b[0]-a[0])
                logsignal = math.log(a[1])*(1-t)+math.log(b[1])*t
            distance = sum((a-b)**2 for a, b in zip(rgb, anchor['rgb_direction']))
            if distance < 1e-10:
                return math.exp(logsignal)
            neighbors.append((distance, logsignal))
        neighbors.sort()
        if not neighbors:
            raise ValueError('No verified anchors cover this area')
        nearest = neighbors[:3]
        weights = [1/d for d, _ in nearest]
        return math.exp(sum(w*p[1] for w, p in zip(weights, nearest))/sum(weights))
