import { describe, expect, it } from "vitest";
import { buildRunMixAxis } from "./dashboard-axis.js";

describe("buildRunMixAxis", function () {
    it("keeps sparse run mixes readable", function () {
        expect(buildRunMixAxis([
            { creates: 1, updates: 1 },
            { conflicts: 3 }
        ])).toEqual({ max: 5, splitNumber: 5 });
    });

    it("steps through small run volumes without jumping straight to 100", function () {
        expect(buildRunMixAxis([{ creates: 6 }])).toEqual({ max: 10, splitNumber: 5 });
        expect(buildRunMixAxis([{ creates: 12, updates: 7 }])).toEqual({ max: 20, splitNumber: 4 });
        expect(buildRunMixAxis([{ creates: 25, updates: 20 }])).toEqual({ max: 50, splitNumber: 5 });
    });

    it("pads larger run volumes to the next hundred", function () {
        expect(buildRunMixAxis([{ creates: 90, updates: 15 }])).toEqual({ max: 200, splitNumber: 5 });
    });

    it("ignores unchanged workers because the chart stacks changed buckets only", function () {
        expect(buildRunMixAxis([{ unchanged: 1000 }])).toEqual({ max: 5, splitNumber: 5 });
    });
});
