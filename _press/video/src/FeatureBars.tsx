import {
  AbsoluteFill,
  Easing,
  Interactive,
  interpolate,
  Sequence,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { FONT, GOLD, MUTED, TEXT } from "./theme";

/**
 * 功能点条 —— 四条，每条 2 秒，共 8 秒。
 *
 * 设计成**下三分之一的横条**而不是整屏卡片：
 * 这四条要压在实机画面上放，整屏卡会把节奏切碎。
 * 背景是半透明黑，直接叠在录屏上即可；
 * 想要纯叠加层的话渲染时加 --codec=prores --prores-profile=4444 出带透明通道的版本
 * （见 README_RENDER.md）。
 */

const ITEMS = [
  { k: "AI 自主战斗", v: "五个分型 × 三档职业，加入队伍自己打完整场" },
  { k: "真实成长", v: "走原版职业链，按档位自动配装，连植入物都会升级" },
  { k: "十名精英", v: "每条线两个，需先练出三档卫兵才解锁" },
  { k: "座舰改装", v: "护卫舰 → 巡洋舰 → 大巡洋舰，外观真的会变" },
];

const PER = 2; // 每条秒数

const Bar: React.FC<{ k: string; v: string }> = ({ k, v }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();

  // 进 0.5s、停 1.1s、出 0.4s
  const enter = interpolate(frame, [0, 0.5 * fps], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.16, 1, 0.3, 1),
  });
  const exit = interpolate(frame, [(PER - 0.4) * fps, PER * fps], [1, 0], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.4, 0, 1, 1),
  });

  return (
    <AbsoluteFill
      name="Bar"
      style={{
        fontFamily: FONT,
        display: "flex",
        justifyContent: "flex-end",
        alignItems: "flex-start",
        paddingLeft: 140,
        paddingBottom: 150,
        opacity: enter * exit,
      }}
    >
      <Interactive.Div
        name="Plate"
        style={{
          display: "flex",
          alignItems: "center",
          gap: 34,
          padding: "30px 56px 30px 40px",
          backgroundColor: "rgba(10,11,13,0.86)",
          borderLeft: `6px solid ${GOLD}`,
          translate: interpolate(frame, [0, 0.5 * fps], ["-60px 0px", "0px 0px"], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          }),
        }}
      >
        <Interactive.Div
          name="Key"
          style={{
            fontSize: 62,
            fontWeight: 700,
            color: TEXT,
            letterSpacing: 3,
            whiteSpace: "nowrap",
          }}
        >
          {k}
        </Interactive.Div>
        <Interactive.Div
          name="Divider"
          style={{ width: 2, height: 54, backgroundColor: GOLD, opacity: 0.55 }}
        />
        <Interactive.Div
          name="Value"
          style={{
            fontSize: 34,
            color: MUTED,
            letterSpacing: 1,
            whiteSpace: "nowrap",
          }}
        >
          {v}
        </Interactive.Div>
      </Interactive.Div>
    </AbsoluteFill>
  );
};

export const FeatureBars: React.FC = () => {
  const { fps } = useVideoConfig();

  return (
    // ★背景透明★ 这四条是要**压在实机画面上**的，不是切换卡。
    // 渲染时必须带 alpha：
    //   npx remotion render FeatureBars out/FeatureBars.mov --codec=prores --prores-profile=4444
    // 渲成 mp4 会得到黑底（mp4 没有透明通道），那就只能当切换卡用了。
    // Studio 里预览时背景显示为棋盘格，是正常的。
    <AbsoluteFill name="FeatureBars">
      {ITEMS.map((it, i) => (
        <Sequence
          key={it.k}
          from={i * PER * fps}
          durationInFrames={PER * fps}
          layout="none"
        >
          <Bar k={it.k} v={it.v} />
        </Sequence>
      ))}
    </AbsoluteFill>
  );
};
