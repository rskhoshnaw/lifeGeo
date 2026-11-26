import { chooseSmallest, compressImage, TARGET_BYTES } from './compressImage.ts'

const blob = (size: number) => new Blob([new Uint8Array(size)])

describe('compressImage', () => {
  it('keeps the finest step that fits and stops there', async () => {
    const sizes = [900_000, 600_000, 300_000, 100_000]
    const tried: number[] = []
    const result = await chooseSmallest(async (step) => { tried.push(step.longSide); return blob(sizes[tried.length - 1]!) })
    expect(result!.size).toBe(300_000)
    expect(tried).toEqual([1600, 1600, 1280])   // the coarsest step was never needed
  })

  it('gives the smallest result when nothing fits, and nothing when the picture cannot be made', async () => {
    const sizes = [900_000, 800_000, 700_000, 750_000]
    let i = 0
    expect((await chooseSmallest(async () => blob(sizes[i++]!)))!.size).toBe(700_000)
    expect(await chooseSmallest(async () => null)).toBeNull()
  })

  it('leaves a small picture, and a picture the browser cannot decode, as they are', async () => {
    const small = new File([new Uint8Array(1000)], 'a.jpg', { type: 'image/jpeg' })
    expect(await compressImage(small)).toBe(small)
    const big = new File([new Uint8Array(TARGET_BYTES + 1)], 'b.jpg', { type: 'image/jpeg' })
    expect(await compressImage(big)).toBe(big)   // jsdom has no image decoding: the original goes (the server still checks the size)
  })
})
