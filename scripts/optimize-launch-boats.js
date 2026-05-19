/**
 * Optimise the five "Straight into the water" boat photos for the
 * launch-stepper cards. The source PNGs live alongside the other
 * brand assets at images/launch/*.png inside the repo (~2 MB each)
 * — too heavy to ship verbatim onto the marketing landing page, so
 * we resize + recompress them into the smaller assets the site
 * actually loads.
 *
 * Resizes each to a Retina-friendly 680×440 (the cards render at
 * 170–340 px on a single column → 340×220 effective at 2× DPR), encodes
 * to JPG (photos compress far better than PNG), and writes the result
 * to site/assets/images/launch/<name>.jpg.
 *
 * Sharp is pulled from the tmp/icon-gen install since it isn't a top-
 * level devDependency. If you delete tmp/icon-gen, run
 * `npm install sharp --no-save` from the repo root first.
 *
 * Idempotent — re-run after touching a PNG and the corresponding JPG
 * regenerates with the same settings.
 */
const path = require('path');
const fs = require('fs');
const sharp = require(path.resolve(__dirname, '..', 'tmp', 'icon-gen', 'node_modules', 'sharp'));

// Source PNGs live in the repo's brand-asset directory next to logos +
// favicons. Keeping the high-res masters in-tree means anyone can
// re-run the optimisation pipeline reproducibly.
const SRC_DIR = path.resolve(__dirname, '..', 'images', 'launch');
const OUT_DIR = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'launch');

const TARGET_WIDTH = 680;
const TARGET_HEIGHT = 440;
const JPG_QUALITY = 82;

// (sourceFile, outputName) tuples — outputName matches the data-boat
// keys used in launch.html so the markup can derive the URL.
const PHOTOS = [
    { src: 'sailboat.png',      out: 'sailboat.jpg' },
    { src: 'speedboat.png',     out: 'speedboat.jpg' },
    { src: 'dazzleship.png',    out: 'dazzleship.jpg' },
    { src: 'containership.png', out: 'containership.jpg' },
    { src: 'passengership.png', out: 'passengership.jpg' },
];

if (!fs.existsSync(OUT_DIR)) {
    fs.mkdirSync(OUT_DIR, { recursive: true });
}

(async () => {
    for (const { src, out } of PHOTOS) {
        const srcPath = path.join(SRC_DIR, src);
        const outPath = path.join(OUT_DIR, out);
        if (!fs.existsSync(srcPath)) {
            console.error(`  missing source: ${srcPath}`);
            continue;
        }
        const inputSize = fs.statSync(srcPath).size;
        await sharp(srcPath)
            // `cover` crops to the target aspect ratio (1.55:1); centred
            // by default, which works for all five photos — the boat
            // sits roughly mid-frame in each source. If a future photo
            // crops badly, switch to `attention` strategy here.
            .resize(TARGET_WIDTH, TARGET_HEIGHT, { fit: 'cover', position: 'center' })
            .jpeg({ quality: JPG_QUALITY, mozjpeg: true })
            .toFile(outPath);
        const outputSize = fs.statSync(outPath).size;
        console.log(`  ${out.padEnd(20)} ${(inputSize / 1024).toFixed(0)}K → ${(outputSize / 1024).toFixed(0)}K`);
    }
})();
