// Check-FontGlyphs.js —— 只读一个 TTF/OTF 的 cmap，回答"这个字符画得出来吗"。
//
// 为什么需要它：RimWorld 的 UI 字体是 Unity 内建的 Arial（Windows 上就取系统 arial.ttf），
// 所以"某个字形能不能显示"= 那个字体有没有对应码点的字形 —— 这是可查的事实，
// 不该靠"我记得 Arial 没有 █"这种印象。
//
// 用法：node Check-FontGlyphs.js <font.ttf> [更多字体...]

const fs = require('fs');

function readTables(buf) {
    const numTables = buf.readUInt16BE(4);
    const tables = {};
    for (let i = 0; i < numTables; i++) {
        const off = 12 + i * 16;
        const tag = buf.toString('ascii', off, off + 4);
        tables[tag] = { offset: buf.readUInt32BE(off + 8), length: buf.readUInt32BE(off + 12) };
    }
    return tables;
}

function parseCmap(buf, base) {
    const numSub = buf.readUInt16BE(base + 2);
    const ranges = [];
    const kinds = [];
    for (let i = 0; i < numSub; i++) {
        const r = base + 4 + i * 8;
        const platform = buf.readUInt16BE(r);
        const encoding = buf.readUInt16BE(r + 2);
        const o = base + buf.readUInt32BE(r + 4);
        const fmt = buf.readUInt16BE(o);
        kinds.push(`(${platform},${encoding})fmt${fmt}`);
        if (fmt === 4) {
            const segCountX2 = buf.readUInt16BE(o + 6);
            const seg = segCountX2 / 2;
            const endBase = o + 14;
            const startBase = endBase + segCountX2 + 2;
            for (let s = 0; s < seg; s++) {
                const end = buf.readUInt16BE(endBase + s * 2);
                const start = buf.readUInt16BE(startBase + s * 2);
                if (start !== 0xffff) ranges.push([start, end]);
            }
        } else if (fmt === 12) {
            const nGroups = buf.readUInt32BE(o + 12);
            for (let g = 0; g < nGroups; g++) {
                const go = o + 16 + g * 12;
                ranges.push([buf.readUInt32BE(go), buf.readUInt32BE(go + 4)]);
            }
        }
    }
    return { ranges, kinds };
}

function has(ranges, cp) {
    for (const [a, b] of ranges) if (cp >= a && cp <= b) return true;
    return false;
}

const SAMPLES = [
    ['✓ U+2713 对勾（项目已用）', 0x2713],
    ['✔ U+2714 粗对勾', 0x2714],
    ['× U+00D7 乘号（项目已用）', 0x00d7],
    ['· U+00B7 间隔点（项目已用）', 0x00b7],
    ['— U+2014 破折号', 0x2014],
    ['→ U+2192 右箭头（项目 UI 已用）', 0x2192],
    ['← U+2190 左箭头', 0x2190],
    ['↳ U+21B3 右弯箭头（草图用它做旁白前缀）', 0x21b3],
    ['└ U+2514 制表转角', 0x2514],
    ['● U+25CF 实心圆', 0x25cf],
    ['○ U+25CB 空心圆', 0x25cb],
    ['◆ U+25C6 实心菱形', 0x25c6],
    ['▶ U+25B6 三角', 0x25b6],
    ['█ U+2588 整块（草图进度条）', 0x2588],
    ['▉ U+2589 七分块', 0x2589],
    ['░ U+2591 亮影块（草图进度条）', 0x2591],
    ['★ U+2605 实心星', 0x2605],
    ['☆ U+2606 空心星', 0x2606],
    ['⏳ U+23F3 沙漏（emoji）', 0x23f3],
    ['✅ U+2705 白粗对勾（emoji）', 0x2705],
    ['🔵 U+1F535 蓝圆（emoji）', 0x1f535],
    ['⚡ U+26A1 闪电（emoji，项目已用？）', 0x26a1],
    ['⚠ U+26A0 警告（项目已用）', 0x26a0],
    ['… U+2026 省略号', 0x2026],
];

for (const file of process.argv.slice(2)) {
    const buf = fs.readFileSync(file);
    const tables = readTables(buf);
    if (!tables.cmap) { console.log(`${file}: 没有 cmap 表`); continue; }
    const { ranges, kinds } = parseCmap(buf, tables.cmap.offset);
    console.log(`\n=== ${file} （cmap 子表：${kinds.join(' ')}，区间 ${ranges.length} 段）`);
    for (const [name, cp] of SAMPLES) {
        console.log(`  ${has(ranges, cp) ? '有' : '缺'}  ${name}`);
    }
}
