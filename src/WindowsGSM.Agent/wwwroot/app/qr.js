// Minimal QR code encoder (byte mode, error correction level M, versions 1–10 — plenty for an
// otpauth:// link). Self-contained so the page needs no third-party script. Follows ISO/IEC 18004:
// Reed–Solomon over GF(256), block interleaving, function patterns, the 8 masks with penalty scoring.

const ECC_PER_BLOCK_M = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
const BLOCKS_M = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

function rawDataModules(ver) {
    let result = (16 * ver + 128) * ver + 64;
    if (ver >= 2) {
        const numAlign = Math.floor(ver / 7) + 2;
        result -= (25 * numAlign - 10) * numAlign - 55;
        if (ver >= 7) result -= 36;
    }
    return result;
}
const totalCodewords = ver => Math.floor(rawDataModules(ver) / 8);
const dataCodewords = ver => totalCodewords(ver) - ECC_PER_BLOCK_M[ver] * BLOCKS_M[ver];

function gfMul(x, y) {
    let z = 0;
    for (let i = 7; i >= 0; i--) {
        z = (z << 1) ^ ((z >>> 7) * 0x11d);
        z ^= ((y >>> i) & 1) * x;
    }
    return z & 0xff;
}

function rsDivisor(degree) {
    const result = new Array(degree).fill(0);
    result[degree - 1] = 1;
    let root = 1;
    for (let i = 0; i < degree; i++) {
        for (let j = 0; j < result.length; j++) {
            result[j] = gfMul(result[j], root);
            if (j + 1 < result.length) result[j] ^= result[j + 1];
        }
        root = gfMul(root, 0x02);
    }
    return result;
}

function rsRemainder(data, divisor) {
    const result = divisor.map(() => 0);
    for (const b of data) {
        const factor = b ^ result.shift();
        result.push(0);
        divisor.forEach((coef, i) => { result[i] ^= gfMul(coef, factor); });
    }
    return result;
}

function alignmentPositions(ver, size) {
    if (ver === 1) return [];
    const numAlign = Math.floor(ver / 7) + 2;
    const step = Math.ceil((ver * 4 + 4) / (numAlign * 2 - 2)) * 2;
    const result = [6];
    for (let pos = size - 7; result.length < numAlign; pos -= step) result.splice(1, 0, pos);
    return result;
}

/** Returns a square boolean matrix (true = dark) for `text`. */
export function qrMatrix(text) {
    const bytes = [...new TextEncoder().encode(text)];
    let ver = 1;
    for (; ver <= 10; ver++) {
        const needBits = 4 + (ver < 10 ? 8 : 16) + bytes.length * 8;
        if (needBits <= dataCodewords(ver) * 8) break;
    }
    if (ver > 10) throw new Error("Text too long for this QR encoder.");

    // ── Data bits ──
    const bits = [];
    const push = (val, len) => { for (let i = len - 1; i >= 0; i--) bits.push((val >>> i) & 1); };
    push(0b0100, 4);
    push(bytes.length, ver < 10 ? 8 : 16);
    for (const b of bytes) push(b, 8);
    const capacity = dataCodewords(ver) * 8;
    push(0, Math.min(4, capacity - bits.length));
    push(0, (8 - bits.length % 8) % 8);
    for (let pad = 0xec; bits.length < capacity; pad ^= 0xec ^ 0x11) push(pad, 8);
    const data = [];
    for (let i = 0; i < bits.length; i += 8) data.push(bits.slice(i, i + 8).reduce((a, b) => (a << 1) | b, 0));

    // ── Error correction + interleaving ──
    const numBlocks = BLOCKS_M[ver];
    const eccLen = ECC_PER_BLOCK_M[ver];
    const raw = totalCodewords(ver);
    const numShort = numBlocks - raw % numBlocks;
    const shortLen = Math.floor(raw / numBlocks);
    const divisor = rsDivisor(eccLen);
    const blocks = [];
    for (let i = 0, k = 0; i < numBlocks; i++) {
        const dat = data.slice(k, k + shortLen - eccLen + (i < numShort ? 0 : 1));
        k += dat.length;
        const ecc = rsRemainder(dat, divisor);
        if (i < numShort) dat.push(-1); // placeholder so short and long blocks line up
        blocks.push(dat.concat(ecc));
    }
    const codewords = [];
    for (let i = 0; i < blocks[0].length; i++) {
        blocks.forEach((block, j) => { if (i !== shortLen - eccLen || j >= numShort) codewords.push(block[i]); });
    }

    // ── Function patterns ──
    const size = ver * 4 + 17;
    const modules = Array.from({ length: size }, () => new Array(size).fill(false));
    const isFunction = Array.from({ length: size }, () => new Array(size).fill(false));
    const set = (x, y, dark) => { modules[y][x] = dark; isFunction[y][x] = true; };

    for (let i = 0; i < size; i++) { set(6, i, i % 2 === 0); set(i, 6, i % 2 === 0); }
    const finder = (cx, cy) => {
        for (let dy = -4; dy <= 4; dy++) for (let dx = -4; dx <= 4; dx++) {
            const x = cx + dx, y = cy + dy;
            if (x < 0 || y < 0 || x >= size || y >= size) continue;
            const dist = Math.max(Math.abs(dx), Math.abs(dy));
            set(x, y, dist !== 2 && dist !== 4);
        }
    };
    finder(3, 3); finder(size - 4, 3); finder(3, size - 4);
    const align = alignmentPositions(ver, size);
    for (let i = 0; i < align.length; i++) for (let j = 0; j < align.length; j++) {
        if ((i === 0 && j === 0) || (i === 0 && j === align.length - 1) || (i === align.length - 1 && j === 0)) continue;
        for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) set(align[i] + dx, align[j] + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }
    const drawFormat = mask => {
        const dataBits = (0 << 3) | mask; // level M = 0b00
        let rem = dataBits;
        for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
        const f = ((dataBits << 10) | rem) ^ 0x5412;
        const bit = i => ((f >>> i) & 1) === 1;
        for (let i = 0; i <= 5; i++) set(8, i, bit(i));
        set(8, 7, bit(6)); set(8, 8, bit(7)); set(7, 8, bit(8));
        for (let i = 9; i < 15; i++) set(14 - i, 8, bit(i));
        for (let i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
        for (let i = 8; i < 15; i++) set(8, size - 15 + i, bit(i));
        set(8, size - 8, true);
    };
    drawFormat(0); // reserve the area; redrawn with the real mask below
    if (ver >= 7) {
        let rem = ver;
        for (let i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >>> 11) * 0x1f25);
        const v = (ver << 12) | rem;
        for (let i = 0; i < 18; i++) {
            const dark = ((v >>> i) & 1) === 1;
            const a = size - 11 + i % 3, b = Math.floor(i / 3);
            set(a, b, dark); set(b, a, dark);
        }
    }

    // ── Codewords, zig-zag ──
    let bitIndex = 0;
    for (let right = size - 1; right >= 1; right -= 2) {
        if (right === 6) right = 5;
        for (let vert = 0; vert < size; vert++) {
            for (let j = 0; j < 2; j++) {
                const x = right - j;
                const upward = ((right + 1) & 2) === 0;
                const y = upward ? size - 1 - vert : vert;
                if (!isFunction[y][x] && bitIndex < codewords.length * 8) {
                    modules[y][x] = ((codewords[bitIndex >>> 3] >>> (7 - (bitIndex & 7))) & 1) === 1;
                    bitIndex++;
                }
            }
        }
    }

    // ── Mask: try all eight, keep the lowest penalty ──
    const maskFn = [
        (x, y) => (x + y) % 2 === 0, (x, y) => y % 2 === 0, (x) => x % 3 === 0, (x, y) => (x + y) % 3 === 0,
        (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0, (x, y) => (x * y) % 2 + (x * y) % 3 === 0,
        (x, y) => ((x * y) % 2 + (x * y) % 3) % 2 === 0, (x, y) => (((x + y) % 2) + (x * y) % 3) % 2 === 0,
    ];
    const applyMask = m => {
        for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (!isFunction[y][x] && maskFn[m](x, y)) modules[y][x] = !modules[y][x];
    };
    let best = 0, bestPenalty = Infinity;
    for (let m = 0; m < 8; m++) {
        applyMask(m);
        drawFormat(m);
        const p = penalty(modules, size);
        if (p < bestPenalty) { best = m; bestPenalty = p; }
        applyMask(m); // undo (XOR)
    }
    applyMask(best);
    drawFormat(best);
    return modules;
}

function penalty(mod, size) {
    let score = 0;
    const lineScore = get => {
        for (let a = 0; a < size; a++) {
            let run = 1;
            for (let b = 1; b <= size; b++) {
                if (b < size && get(a, b) === get(a, b - 1)) { run++; continue; }
                if (run >= 5) score += 3 + (run - 5);
                run = 1;
            }
            // finder-like 1:1:3:1:1 with 4 light modules on either side
            for (let b = 0; b + 10 < size; b++) {
                const seq = [1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0];
                let fwd = true, rev = true;
                for (let k = 0; k < 11; k++) {
                    const v = get(a, b + k) ? 1 : 0;
                    if (v !== seq[k]) fwd = false;
                    if (v !== seq[10 - k]) rev = false;
                }
                if (fwd) score += 40;
                if (rev) score += 40;
            }
        }
    };
    lineScore((a, b) => mod[a][b]);
    lineScore((a, b) => mod[b][a]);
    let dark = 0;
    for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
        if (mod[y][x]) dark++;
        if (x + 1 < size && y + 1 < size) {
            const c = mod[y][x];
            if (c === mod[y][x + 1] && c === mod[y + 1][x] && c === mod[y + 1][x + 1]) score += 3;
        }
    }
    const total = size * size;
    score += Math.max(0, Math.ceil(Math.abs(dark * 20 - total * 10) / total) - 1) * 10;
    return score;
}

/** The QR code as an SVG element (black on white with the standard 4-module quiet zone). */
export function qrSvg(text, { label = "QR code" } = {}) {
    const m = qrMatrix(text);
    const size = m.length + 8;
    const ns = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", `0 0 ${size} ${size}`);
    svg.setAttribute("role", "img");
    svg.setAttribute("aria-label", label);
    svg.setAttribute("shape-rendering", "crispEdges");
    const bg = document.createElementNS(ns, "rect");
    bg.setAttribute("width", size); bg.setAttribute("height", size); bg.setAttribute("fill", "#ffffff");
    let d = "";
    m.forEach((row, y) => row.forEach((dark, x) => { if (dark) d += `M${x + 4} ${y + 4}h1v1h-1z`; }));
    const path = document.createElementNS(ns, "path");
    path.setAttribute("d", d);
    path.setAttribute("fill", "#000000");
    svg.append(bg, path);
    return svg;
}
