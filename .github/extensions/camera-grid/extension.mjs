import { createCanvas, joinSession } from '@github/copilot-sdk/extension';
import { cameraCanvasOptions } from '../../../spike/mxc-visual/camera-canvas.mjs';

await joinSession({ canvases: [createCanvas(cameraCanvasOptions())] });
