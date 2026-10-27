import { doctorsFromCsv, parseCsv, placesFromCsv } from './csv.ts'
import starter from '../../../../docs/places-starter.csv?raw'

describe('CSV', () => {
  it('reads quotes, commas inside quotes, doubled quotes, CRLF and a byte order mark', () => {
    expect(parseCsv('\uFEFFa,b\r\n"x, y","say ""hi"""\r\n\r\nlast,')).toEqual([['a', 'b'], ['x, y', 'say "hi"'], ['last', '']])
    expect(parseCsv('a,"two\nlines"')).toEqual([['a', 'two\nlines']])
    expect(parseCsv('')).toEqual([])
  })

  it('turns the file into import rows: each kind needs the levels above it, others are dropped', () => {
    const text = [
      'kind,city,neighbourhood,street,name,name_ckb,name_ar,name_en,status',
      'city,,,,Erbil,هەولێر,أربيل,Erbil,active',
      'neighbourhood,Erbil,,,Ankawa,,,,',
      'STREET,Erbil,Ankawa,,Main Street,,,,inactive',
      'alley,Erbil,Ankawa,Main Street,Alley 1,,,,',
      'alley,Erbil,Ankawa,,Alley 2,,,,',
      'street,Erbil,,,No neighbourhood,,,,',
      'neighbourhood,,,,No city,,,,',
      'city,Erbil,Ankawa,,City with a neighbourhood,,,,',
      'road,Erbil,,,Bad kind,,,,',
      'city,,,,,,,,',
    ].join('\n')
    const rows = placesFromCsv(text)
    expect(rows.map((r) => [r.kind, r.name, r.city, r.neighbourhood, r.street, r.active])).toEqual([
      ['city', 'Erbil', null, null, null, true],
      ['neighbourhood', 'Ankawa', 'Erbil', null, null, true],
      ['street', 'Main Street', 'Erbil', 'Ankawa', null, false],
      ['alley', 'Alley 1', 'Erbil', 'Ankawa', 'Main Street', true],
      ['alley', 'Alley 2', 'Erbil', 'Ankawa', null, true],
    ])
    expect(rows[0]).toMatchObject({ nameCkb: 'هەولێر', nameAr: 'أربيل', nameEn: 'Erbil' })
  })

  it('reads an exported file again: id columns are ignored, a protected formula is restored, active=no hides', () => {
    const text = 'id,parent_id,kind,city,neighbourhood,street,name,name_ckb,name_ar,name_en,status,active\r\n'
      + 'u1,,city,Erbil,,,"Erbil, old",,,,active,\r\n'
      + "u2,u1,neighbourhood,Erbil,,,'=Hood,,,,,no\r\n"
      + "u3,u1,neighbourhood,Erbil,,,'normal,,,,,\r\n"
    expect(placesFromCsv(text).map((r) => [r.name, r.active])).toEqual([['Erbil, old', true], ['=Hood', false], ["'normal", true]])
  })

  it('accepts a file with only some of the columns and refuses one without a header', () => {
    expect(placesFromCsv('kind,name\ncity,Zakho')).toEqual([
      { kind: 'city', name: 'Zakho', city: null, neighbourhood: null, street: null, nameCkb: null, nameAr: null, nameEn: null, active: true },
    ])
    expect(placesFromCsv('')).toEqual([])
    expect(placesFromCsv('name\nX')).toEqual([])
  })

  it('reads the starter file: seven cities with three names each and the Erbil neighbourhoods', () => {
    const rows = placesFromCsv(starter)
    const cities = rows.filter((r) => r.kind === 'city')
    expect(cities.map((r) => r.name)).toEqual(['Erbil', 'Sulaymaniyah', 'Duhok', 'Zakho', 'Kirkuk', 'Ranya', 'Mosul'])
    expect(cities.every((r) => r.nameCkb && r.nameAr && r.nameEn)).toBe(true)
    const hoods = rows.filter((r) => r.kind === 'neighbourhood')
    expect(hoods.length).toBe(6)
    expect(hoods.every((r) => r.city === 'Erbil' && r.nameCkb && r.nameAr)).toBe(true)
    expect(rows.length).toBe(13)   // no row of the file is dropped as invalid
  })
})

describe('doctorsFromCsv', () => {
  it('maps the columns, turns empty cells into null and yes/no into booleans', () => {
    const text = [
      'name,clinic_name,specialty,cooperates,dealer,class,phone,city,neighbourhood,latitude,longitude,notes,visitor_email',
      'Dr. Ali,Noor,neurology,yes,no,A,0750,Erbil,Ankawa,36.2201,43.9924,"a, b",rep@t.test',
      "Dr. Zana,,,,,,,,,,,'=cmd,",
      ',,,,,,,,,,,,',
    ].join('\n')
    const rows = doctorsFromCsv(text)
    expect(rows).toHaveLength(2)   // a completely empty line is dropped by the parser
    expect(rows[0]).toMatchObject({ name: 'Dr. Ali', clinicName: 'Noor', specialty: 'neurology', cooperates: true, dealer: false, customerClass: 'A', city: 'Erbil', latitude: 36.2201, longitude: 43.9924, notes: 'a, b', visitorEmail: 'rep@t.test' })
    expect(rows[1]).toMatchObject({ name: 'Dr. Zana', clinicName: null, cooperates: null, latitude: null, notes: '=cmd', visitorEmail: null })
    expect(doctorsFromCsv('')).toEqual([])
    expect(doctorsFromCsv('name,latitude\nDr. X,abc')[0]?.latitude).toBeNull()
  })
})
