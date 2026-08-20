/**
 * Dynasty Retinue & Refit —— 宣传片插入片段的共用样式。
 *
 * 配色直接取自 mod 自己的面板（Main.cs 里那几个 rich-text 颜色），
 * 这样视频里的卡片和玩家真正会看到的界面是同一套视觉语言。
 */

export const GOLD = "#c8a45c"; // 面板里的强调色
export const BLUE = "#7ec8ff"; // 信息/数值
export const GREEN = "#7ec87e"; // 正向
export const AMBER = "#d0a050"; // 提示
export const INK = "#0a0b0d"; // 底
export const INK2 = "#14161a"; // 次级底
export const TEXT = "#e8e4da"; // 正文
export const MUTED = "#8d8677"; // 弱化

/**
 * 中文字体走系统栈而不是 Google Fonts。
 * CJK 网络字体动辄几 MB，而这三个片段总共不到 20 秒 ——
 * 为它引一个下载依赖不划算，而且渲染是在本机跑的，系统字体一定在。
 */
export const FONT =
  '"Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans SC", "Source Han Sans SC", sans-serif';

/** 等宽（数字对齐用）。数值动画里数字会跳动，不等宽会左右抖。 */
export const MONO =
  '"Cascadia Mono", Consolas, "Courier New", ui-monospace, monospace';
