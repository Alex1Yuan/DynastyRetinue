import { Composition } from "remotion";
import { TitleCard } from "./TitleCard";
import { FeatureBars } from "./FeatureBars";
import { ShipStats } from "./ShipStats";
import { UninstallCard, EndCard } from "./Cards";
import { Film } from "./Film";

/**
 * `Film` 是成片，直接渲出来就能发。
 *
 * 下面几个是它的组成部分，单独注册是为了能在 Studio 里一个个调 ——
 * 在 3 分半的时间轴上拖到第 2 分 20 秒去看一个 8 秒的动画，太费事了。
 */
export const MyComposition = () => {
  return (
    <>
      <Composition
        id="Film"
        component={Film}
        durationInFrames={60 * 212} // 13+4+24+26+36+37+30+36+6 = 212s = 3:32
        fps={60}
        width={1920}
        height={1080}
      />
      <Composition
        id="UninstallCard"
        component={UninstallCard}
        durationInFrames={60 * 36}
        fps={60}
        width={1920}
        height={1080}
      />
      <Composition
        id="EndCard"
        component={EndCard}
        durationInFrames={60 * 6}
        fps={60}
        width={1920}
        height={1080}
      />
      <Composition
        id="TitleCard"
        component={TitleCard}
        durationInFrames={180} // 3s @60fps
        fps={60}
        width={1920}
        height={1080}
      />
      <Composition
        id="FeatureBars"
        component={FeatureBars}
        durationInFrames={480} // 4 条 × 2s
        fps={60}
        width={1920}
        height={1080}
      />
      <Composition
        id="ShipStats"
        component={ShipStats}
        durationInFrames={480} // 8s
        fps={60}
        width={1920}
        height={1080}
      />
    </>
  );
};
