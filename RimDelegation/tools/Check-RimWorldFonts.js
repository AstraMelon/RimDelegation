// Check-RimWorldFonts.js —— 从 Unity 的 `unity default resources` 里把内建字体挖出来，
// 解析它们的 cmap，回答"游戏里到底画得出来哪些符号"。
//
// 背景（已核实）：`Verse.Text` 里的字体字面量是 `Fonts/Arial_small` / `Fonts/Arial_medium`
// —— 它们**不是** Windows 的 arial.ttf，而是 Unity 播放器打包在
// `RimWorldWin64_Data/Resources/unity default resources` 里的内建字体。
// 所以"字符能不能显示"必须看这个文件里的字体，不能拿系统的 arial.ttf 代替
// （系统的 Arial 缺 U+2713，而原版 Debug UI 里明明在用 ✓ ⇒ 两者字形集不同）。
//
// 用法：node Check-RimWorldFonts.js <unity default resources 路径>

const fs = require('fs');

const SAMPLES = [
    ['✓ U+2713 对勾（原版 Debug UI 用过 11 处）', 0x2713],
    ['× U+00D7 乘号（项目已用）', 0x00d7],
    ['· U+00B7 间隔点（项目已用）', 0x00b7],
    ['— U+2014 破折号', 0x2014],
    ['… U+2026 省略号', 0x2026],
    ['→ U+2192 右箭头（项目 UI 已用）', 0x2192],
    ['↳ U+21B3 右弯箭头（草图用它做旁白前缀）', 0x21b3],
    ['└ U+2514 制表转角', 0x2514],
    ['● U+25CF 实心圆（想当"进行中"标记）', 0x25cf],
    ['○ U+25CB 空心圆', 0x25cb],
    ['◆ U+25C6 实心菱形', 0x25c6],
    ['▶ U+25B6 三角', 0x25b6],
    ['█ U+2588 整块（草图进度条）', 0x2588],
    ['░ U+2591 亮影块（草图进度条）', 0x2591],
    ['★ U+2605 实心星', 0x2605],
    ['⚠ U+26A0 警告（项目提示文本已用！）', 0x26a0],
    ['⚡ U+26A1 闪电（项目加班按钮文案）', 0x26a1],
    ['⏳ U+23F3 沙漏（emoji）', 0x23f3],
    ['✅ U+2705 白粗对勾（emoji）', 0x2705],
    ['🔵 U+1F535 蓝圆（emoji）', 0x1f535],
];

function parseCmap(buf, base) {
    const numSub = buf.readUInt16BE(base + 2);
    const ranges = [];
    for (let i = 0; i < numSub; i++) {
        const r = base + 4 + i * 8;
        const o = base + buf.readUInt32BE(r + 4);
        if (o + 4 > buf.length) continue;
        const fmt = buf.readUInt16BE(o);
        if (fmt === 4) {
            const segCountX2 = buf.readUInt16BE(o + 6);
            const seg = segCountX2 / 2;
            const endBase = o + 14;
            const startBase = endBase + segCountX2 + 2;
            if (startBase + segCountX2 > buf.length) continue;
            for (let s = 0; s < seg; s++) {
                const end = buf.readUInt16BE(endBase + s * 2);
                const start = buf.readUInt16BE(startBase + s * 2);
                if (start !== 0xffff && start <= end) ranges.push([start, end]);
            }
        } else if (fmt === 12) {
            const nGroups = buf.readUInt32BE(o + 12);
            for (let g = 0; g < nGroups; g++) {
                const go = o + 16 + g * 12;
                if (go + 12 > buf.length) break;
                ranges.push([buf.readUInt32BE(go), buf.readUInt32BE(go + 4)]);
            }
        }
    }
    return ranges;
}

function has(ranges, cp) {
    for (const [a, b] of ranges) if (cp >= a && cp <= b) return true;
    return false;
}

const path = process.argv[2];
const buf = fs.readFileSync(path);
const found = [];
// 宽松扫描：字体的 ttf 字节可能被 Unity 塞在 .resS 这种大流里（表目录的 offset 是相对流起点的），
// 所以先定位 'cmap' 这个 ASCII 标记，再往回最多 4KB 找 ttf 头（表目录最多 40 项 = 640 字节）。
let cursor = 0;
while (true) {
    const c = buf.indexOf('cmap', cursor, 'latin1');
    if (c < 0) break;
    cursor = c + 4;
    for (let back = 12; back <= 4096; back++) {
        const i = c - back;
        if (i < 0) break;
        if (buf.readUInt32BE(i) !== 0x00010000) continue;
        const numTables = buf.readUInt16BE(i + 4);
        if (numTables < 5 || numTables > 40) continue;
        let cmapOff = -1;
        let ok = true;
        for (let t = 0; t < numTables; t++) {
            const o = i + 12 + t * 16;
            if (o + 16 > buf.length) { ok = false; break; }
            const tag = buf.toString('latin1', o, o + 4);
            if (!/^[\x20-\x7E]{4}$/.test(tag)) { ok = false; break; }
            if (tag === 'cmap') cmapOff = i + buf.readUInt32BE(o + 8);
        }
        if (ok && cmapOff >= 0 && cmapOff + 4 <= buf.length) {
            found.push({ at: i, numTables, cmapOff });
            break;
        }
    }
}

console.log(`在 ${path} 里找到 ${found.length} 个候选字体`);
for (const f of found) {
    let ranges = [];
    try { ranges = parseCmap(buf, f.cmapOff); } catch (e) { console.log(`  偏移 ${f.at}: cmap 解析失败 ${e.message}`); continue; }
    console.log(`\n=== 字体 @偏移 ${f.at}（表 ${f.numTables} 个，cmap 区间 ${ranges.length} 段）`);
    for (const [name, cp] of SAMPLES) {
        console.log(`  ${has(ranges, cp) ? '有' : '缺'}  ${name}`);
    }
}
