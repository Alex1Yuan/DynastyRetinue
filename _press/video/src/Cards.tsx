import {
  AbsoluteFill,
  Easing,
  Interactive,
  interpolate,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { AMBER, FONT, GOLD, GREEN, INK, MONO, MUTED, TEXT } from "./theme";

/**
 * 卸载三条路径。
 *
 * ★这段为什么用卡片而不是实机★
 *   "不按流程卸载"的画面（洋红座舰、卡死的卫兵）只在一次一次性的测试里出现过，
 *   没留成文件，重现要再把存档搞坏一次。与其摆拍，不如把测出来的结论直接写清楚 ——
 *   这段配音信息密度也是全片最高的，本来就该配字幕而不是让人分心看画面。
 *
 * 三行内容全部来自实测（README「卸载」一节的同一张表），不是推测。
 */

type Path = {
  how: string;
  result: string;
  tone: "good" | "warn" | "fix";
  note: string;
};

const PATHS: Path[] = [
  {
    how: "先遣散 · 再还原 · 存盘",
    result: "零残留",
    tone: "good",
    note: "面板上两个按钮的事",
  },
  {
    how: "不清理，直接删 mod",
    result: "存档不坏，但有残留",
    tone: "warn",
    note: "卫兵变成删不掉的雕像，座舰贴图变洋红",
  },
  {
    how: "把 mod 装回来，再正常清理",
    result: "完全恢复",
    tone: "fix",
    note: "上面那种情况可以救",
  },
];

const COLOR = { good: GREEN, warn: AMBER, fix: "#7ec8ff" } as const;

const Row: React.FC<{ p: Path; i: number }> = ({ p, i }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const t0 = (1.0 + i * 1.1) * fps;
  const appear = interpolate(frame, [t0, t0 + 0.5 * fps], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.16, 1, 0.3, 1),
  });

  return (
    <Interactive.Div
      name={`Path_${i}`}
      style={{
        display: "grid",
        gridTemplateColumns: "620px 1fr",
        alignItems: "center",
        gap: 40,
        padding: "26px 34px",
        marginBottom: 18,
        borderLeft: `5px solid ${COLOR[p.tone]}`,
        background: "rgba(255,255,255,0.028)",
        opacity: appear,
        translate: interpolate(frame, [t0, t0 + 0.5 * fps], ["0px 30px", "0px 0px"], {
          extrapolateLeft: "clamp",
          extrapolateRight: "clamp",
          easing: Easing.bezier(0.16, 1, 0.3, 1),
        }),
      }}
    >
      <Interactive.Div name="How" style={{ fontSize: 40, color: TEXT, letterSpacing: 1 }}>
        {p.how}
      </Interactive.Div>
      <Interactive.Div name="Right">
        <Interactive.Div
          name="Result"
          style={{ fontSize: 44, color: COLOR[p.tone], fontWeight: 700, letterSpacing: 1 }}
        >
          {p.result}
        </Interactive.Div>
        <Interactive.Div name="Note" style={{ fontSize: 27, color: MUTED, marginTop: 8 }}>
          {p.note}
        </Interactive.Div>
      </Interactive.Div>
    </Interactive.Div>
  );
};

export const UninstallCard: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();

  return (
    <AbsoluteFill
      name="UninstallCard"
      style={{
        backgroundColor: INK,
        fontFamily: FONT,
        padding: "120px 160px 200px",
        display: "flex",
        flexDirection: "column",
        justifyContent: "center",
      }}
    >
      <Interactive.Div
        name="Title"
        style={{
          fontSize: 46,
          color: GOLD,
          letterSpacing: 5,
          marginBottom: 12,
          opacity: interpolate(frame, [0, 0.55 * fps], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          }),
        }}
      >
        卸载
      </Interactive.Div>
      <Interactive.Div
        name="Sub"
        style={{
          fontSize: 28,
          color: MUTED,
          marginBottom: 46,
          letterSpacing: 1,
          opacity: interpolate(frame, [0.2 * fps, 0.8 * fps], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
          }),
        }}
      >
        以下三条都实测过，不是推测
      </Interactive.Div>

      {PATHS.map((p, i) => (
        <Row key={p.how} p={p} i={i} />
      ))}
    </AbsoluteFill>
  );
};

/** 尾板：仓库地址 + 协议。信息就这么点，不要堆。 */
export const EndCard: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const fade = (d: number) =>
    interpolate(frame, [d * fps, (d + 0.7) * fps], [0, 1], {
      extrapolateLeft: "clamp",
      extrapolateRight: "clamp",
      easing: Easing.bezier(0.16, 1, 0.3, 1),
    });

  return (
    <AbsoluteFill
      name="EndCard"
      style={{
        backgroundColor: INK,
        fontFamily: FONT,
        display: "flex",
        flexDirection: "column",
        justifyContent: "center",
        alignItems: "center",
        gap: 26,
      }}
    >
      <Interactive.Div
        name="Name"
        style={{ fontSize: 86, color: GOLD, letterSpacing: 12, opacity: fade(0) }}
      >
        家族卫队 · 座舰改装
      </Interactive.Div>
      <Interactive.Div
        name="NameEn"
        style={{ fontSize: 38, color: TEXT, letterSpacing: 8, opacity: fade(0.25) }}
      >
        Dynasty Retinue &amp; Refit
      </Interactive.Div>
      <Interactive.Div
        name="Repo"
        style={{
          fontSize: 32,
          color: MUTED,
          fontFamily: MONO,
          marginTop: 34,
          opacity: fade(0.7),
        }}
      >
        github.com/Alex1Yuan/DynastyRetinue
      </Interactive.Div>
      <Interactive.Div
        name="Lic"
        style={{ fontSize: 26, color: MUTED, letterSpacing: 2, opacity: fade(0.95) }}
      >
        MIT 开源　·　Unity Mod Manager
      </Interactive.Div>
    </AbsoluteFill>
  );
};
