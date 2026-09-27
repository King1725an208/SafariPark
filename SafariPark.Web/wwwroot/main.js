// Bootstrap: start the .NET runtime, create the renderer + game through InitAsync, then pump
// requestAnimationFrame. All gameplay input is forwarded through the [JSExport] surface —
// DOM events are normalized here (codes, not characters) so the managed side maps them.
import { dotnet } from './_framework/dotnet.js';

const status = document.getElementById('hud-status');
const canvas = document.getElementById('gpu-canvas');

try {
    if (!navigator.gpu) throw new Error('此浏览器不支持 WebGPU（需要 Chrome/Edge 113+，HTTPS 或 localhost）');

    const { getAssemblyExports, getConfig } = await dotnet.create();
    const exports = await getAssemblyExports(getConfig().mainAssemblyName);
    const program = exports.SafariPark.Web.Program;

    const query = new URLSearchParams(location.search);
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.floor(canvas.clientWidth * dpr);
    canvas.height = Math.floor(canvas.clientHeight * dpr);


    // The only GLB the lite build mounts is the kitten; trees are procedural.
    // The simulation reads assets synchronously, so bytes land in the managed
    // table before InitAsync builds the world.
    const assets = ['cat.glb'];
    status.textContent = '下载模型…';
    await Promise.all(assets.map(async (name) => {
        const response = await fetch(`assets/${name}`);
        if (!response.ok) throw new Error(`asset ${name}: HTTP ${response.status}`);
        program.ProvideAsset(name, new Uint8Array(await response.arrayBuffer()));
    }));

    const hostModuleUrl = new URL('safari-host.js', document.baseURI).href;
    await program.InitAsync(hostModuleUrl, canvas.width, canvas.height, query.get('log') || '');
    window.__safariProgram = program; // QA hooks reachable from the console

    let lastTs = 0;
    const frame = (ts) => {
        lastTs = ts;
        program.OnAnimationFrame(ts);
        requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);

    // ------- input bridge -------
    window.addEventListener('keydown', (e) => {
        if (e.repeat) return;
        if (['Tab', 'Space', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.code))
            e.preventDefault();
        program.OnKey(e.code, true);
    });
    window.addEventListener('keyup', (e) => program.OnKey(e.code, false));
    // Release every mapped key on blur so a held WASD can't stick while unfocused.
    window.addEventListener('blur', () => {
        for (const code of ['KeyW', 'KeyA', 'KeyS', 'KeyD', 'KeyQ', 'KeyE', 'KeyR', 'KeyF',
            'Space', 'Tab', 'ShiftLeft', 'ShiftRight', 'Escape',
            'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'])
            program.OnKey(code, false);
    });

    canvas.addEventListener('pointerdown', (e) => {
        canvas.setPointerCapture(e.pointerId);
        program.OnPointerDown(e.offsetX * dpr, e.offsetY * dpr, e.button);
    });
    canvas.addEventListener('pointerup', (e) =>
        program.OnPointerUp(e.offsetX * dpr, e.offsetY * dpr, e.button));
    canvas.addEventListener('pointermove', (e) => {
        if (e.buttons & 1) program.OnPointerDrag(e.movementX * dpr, e.movementY * dpr);
    });
    canvas.addEventListener('wheel', (e) => {
        e.preventDefault();
        program.OnScroll(e.offsetX * dpr, e.offsetY * dpr, e.deltaY);
    }, { passive: false });
    canvas.addEventListener('contextmenu', (e) => e.preventDefault());

    new ResizeObserver(() => {
        const w = Math.floor(canvas.clientWidth * dpr);
        const h = Math.floor(canvas.clientHeight * dpr);
        if (w > 0 && h > 0) { canvas.width = w; canvas.height = h; program.OnResize(w, h); }
    }).observe(canvas);
} catch (error) {
    status.textContent = `SAFARI-FAIL: ${error && error.message ? error.message : error}`;
    console.error(error);
}
