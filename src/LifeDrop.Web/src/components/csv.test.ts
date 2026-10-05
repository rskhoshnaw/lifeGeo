import { parseCsv, placesFromCsv } from './csv.ts'

describe('CSV', () => {
  it('reads quotes, commas inside quotes, doubled quotes, CRLF and a byte order mark', () => {
    expect(parseCsv('\uFEFFa,b\r\n"x, y","say ""hi"""\r\n\r\nlast,')).toEqual([['a', 'b'], ['x, y', 'say "hi"'], ['last', '']])
    expect(parseCsv('a,"two\nlines"')).toEqual([['a', 'two\nlines']])
    expect(parseCsv('')).toEqual([])
  })

  it('turns the export file into import rows and drops unusable rows', () => {
    const text = [
      'city,kind,name_ckb,name_ar,name_en,name_local,osm,active',
      'Erbil,street,,,,100m Road,way/1,yes',
      'Erbil,STREET,,,Only English,,way/2,',
      'Erbil,neighbourhood,گەڕەک,,,,node/3,no',
      'Erbil,road,,,,Bad kind,,yes',
      ',street,,,,No city,,yes',
      'Erbil,street,,,,,,yes',
    ].join('\n')
    expect(placesFromCsv(text)).toEqual([
      { city: 'Erbil', kind: 'street', name: '100m Road', nameCkb: null, nameAr: null, nameEn: null, osm: 'way/1', active: true },
      { city: 'Erbil', kind: 'street', name: 'Only English', nameCkb: null, nameAr: null, nameEn: 'Only English', osm: 'way/2', active: true },
      { city: 'Erbil', kind: 'neighbourhood', name: 'گەڕەک', nameCkb: 'گەڕەک', nameAr: null, nameEn: null, osm: 'node/3', active: false },
    ])
  })

  it('accepts a file with only some of the columns and refuses one without a header', () => {
    expect(placesFromCsv('city,kind,name_local\nZakho,street,Main')).toEqual([
      { city: 'Zakho', kind: 'street', name: 'Main', nameCkb: null, nameAr: null, nameEn: null, osm: null, active: true },
    ])
    expect(placesFromCsv('')).toEqual([])
    expect(placesFromCsv('name\nX')).toEqual([])
  })
})
