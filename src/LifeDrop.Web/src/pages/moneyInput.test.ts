import { describe, expect, it } from 'vitest'
import { parseMoney } from './moneyInput.ts'

describe('money typed by a person', () => {
  it.each([
    ['10440', 10440], ['10.440', 10440], ['10,440', 10440], ['۱۰٬۴۴۰', 10440], ['١٠٬٤٤٠', 10440], ['10 440', 10440], ['10 440', 10440],
    ['1.234.567', 1234567], ['1,234,567', 1234567], ['0', 0], ['500', 500], ['0.500', 500],
  ])('IQD %s is %d', (text, value) => {
    expect(parseMoney(text, 'IQD')).toEqual({ value })
  })

  it.each(['10.44', '10,5', '1.5', '10.4400', '12.34.5'])('IQD refuses the fraction %s', (text) => {
    expect(parseMoney(text, 'IQD').problem).toBeDefined()
  })

  it('names the fraction problem and the empty and garbage cases', () => {
    expect(parseMoney('10.44', 'IQD')).toEqual({ problem: 'decimals' })
    expect(parseMoney('', 'IQD')).toEqual({ problem: 'empty' })
    expect(parseMoney('abc', 'IQD')).toEqual({ problem: 'invalid' })
    expect(parseMoney('10..5', 'IQD')).toEqual({ problem: 'invalid' })
    expect(parseMoney('-5', 'IQD')).toEqual({ problem: 'invalid' })
  })

  it.each([
    ['12', 12], ['12.5', 12.5], ['12,5', 12.5], ['12.50', 12.5], ['1,234.56', 1234.56], ['1.234,56', 1234.56], ['۱۲٫۵', 12.5], ['1,500', 1500],
  ])('USD %s is %d', (text, value) => {
    expect(parseMoney(text, 'USD')).toEqual({ value })
  })

  it('USD refuses more than two decimals', () => {
    expect(parseMoney('12.345', 'USD')).toEqual({ value: 12345 })   // three digits after one mark are thousands, as in Iraq
    expect(parseMoney('12.3456', 'USD')).toEqual({ problem: 'decimals' })
    expect(parseMoney('1,234.567', 'USD')).toEqual({ problem: 'decimals' })
  })
})
