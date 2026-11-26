// Making a picture small in the browser before it is uploaded (plan step 5.58): the best quality that still fits is kept.
// The steps go from the finest to the coarsest; the first result at or below the target is used.
export const STEPS: { longSide: number; quality: number }[] = [
  { longSide: 1600, quality: 0.85 },
  { longSide: 1600, quality: 0.75 },
  { longSide: 1280, quality: 0.7 },
  { longSide: 1024, quality: 0.6 },
]
export const TARGET_BYTES = 400 * 1024

/** The first encoded picture within the target, otherwise the smallest one. `encode` may return null (the picture cannot be made). */
export async function chooseSmallest(encode: (step: { longSide: number; quality: number }) => Promise<Blob | null>, target = TARGET_BYTES): Promise<Blob | null> {
  let smallest: Blob | null = null
  for (const step of STEPS) {
    const blob = await encode(step)
    if (!blob) return smallest
    if (blob.size <= target) return blob
    if (!smallest || blob.size < smallest.size) smallest = blob
  }
  return smallest
}

/** A JPEG made from the file (at most `longSide` pixels), or null when the browser cannot decode or draw it. */
async function encodeJpeg(file: Blob, longSide: number, quality: number): Promise<Blob | null> {
  if (typeof createImageBitmap !== 'function' || typeof document === 'undefined') return null
  try {
    const bitmap = await createImageBitmap(file)
    const scale = Math.min(1, longSide / Math.max(bitmap.width, bitmap.height))
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(bitmap.width * scale))
    canvas.height = Math.max(1, Math.round(bitmap.height * scale))
    const context = canvas.getContext('2d')
    if (!context) return null
    context.fillStyle = '#fff'   // a transparent PNG becomes white instead of black
    context.fillRect(0, 0, canvas.width, canvas.height)
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
    bitmap.close?.()
    return await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', quality))
  } catch {
    return null
  }
}

/** The smaller picture, or the file itself when it cannot be made smaller or is already small enough (a picture is never made bigger). */
export async function compressImage(file: File): Promise<Blob> {
  if (file.size <= TARGET_BYTES) return file
  const smaller = await chooseSmallest((step) => encodeJpeg(file, step.longSide, step.quality))
  return smaller && smaller.size < file.size ? smaller : file
}
