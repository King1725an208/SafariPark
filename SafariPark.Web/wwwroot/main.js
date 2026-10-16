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


    // Every GLB the park mounts (animals + scenery). The simulation reads assets
    // synchronously, so all bytes land in the managed table before InitAsync builds the world.
    const assets = [
        'alpaca.glb', 'cat.glb', 'deer.glb', 'fox.glb', 'horse.glb', 'husky.glb',
        'muledeer.glb', 'rabbit.glb', 'ranger.glb', 'stag.glb', 'wolf.glb',
        'pine1.glb', 'pine2.glb', 'pine3.glb', 'twist1.glb', 'twist2.glb', 'twist3.glb',
        'rock1.glb', 'rock2.glb', 'rock3.glb', 'plant.glb', 'plantbig.glb',
    ];
    const packUrl = query.get('pack');
    status.textContent = '下载模型…';
    if (packUrl) {
        const response = await fetch(packUrl);
        if (!response.ok) throw new Error(`asset pack: HTTP ${response.status}`);
        program.ProvideAssetPack(new Uint8Array(await response.arrayBuffer()));
    } else {
        await Promise.all(assets.map(async (name) => {
            const response = await fetch(`assets/${name}`);
            if (!response.ok) throw new Error(`asset ${name}: HTTP ${response.status}`);
            program.ProvideAsset(name, new Uint8Array(await response.arrayBuffer()));
        }));
    }

    const hostModuleUrl = new URL('safari-host.js', document.baseURI).href;
    await program.InitAsync(hostModuleUrl, canvas.width, canvas.height, query.get('log') || '');
    window.__safariProgram = program; // roster click handlers reach back through here

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

    document.getElementById('prev-btn').addEventListener('click', () => program.OnCycleAnimal(-1));
    document.getElementById('next-btn').addEventListener('click', () => program.OnCycleAnimal(1));
} catch (error) {
    status.textContent = `SAFARI-FAIL: ${error && error.message ? error.message : error}`;
    console.error(error);
}
