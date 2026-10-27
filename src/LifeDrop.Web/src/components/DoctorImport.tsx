import { useState, type ChangeEvent } from 'react'
import { AdminApi, errorMessage, type DoctorImportResult, type DoctorImportRow } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { doctorsFromCsv, doctorsTemplateCsv } from './csv.ts'
import { downloadText } from './download.ts'

// Import of a doctor list from a CSV file (plan step 5.9, docs/doctors-import.md). The file is checked first (nothing is created); only a clean check can be imported.
// A doctor that already exists (same name and phone) is skipped, so the same file can be sent again.
export function DoctorImport({ admin, onImported }: { admin: AdminApi; onImported: () => void }) {
  const [rows, setRows] = useState<DoctorImportRow[] | null>(null)
  const [checked, setChecked] = useState<DoctorImportResult | null>(null)
  const [done, setDone] = useState<DoctorImportResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function run(dryRun: boolean, data: DoctorImportRow[]) {
    setBusy(true)
    setError(null)
    try {
      const result = await admin.importDoctors(data, dryRun)
      if (dryRun) setChecked(result)
      else {
        setDone(result)
        setRows(null)
        setChecked(null)
        onImported()
      }
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  async function onFile(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setDone(null)
    setChecked(null)
    const parsed = doctorsFromCsv(await file.text())
    if (parsed.length === 0) {
      setRows(null)
      return setError(t('web.doctorImport.badFile'))
    }
    setRows(parsed)
    await run(true, parsed)
  }

  const clean = checked !== null && checked.errors.length === 0

  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.doctorImport.title')}</h3>
      <p className="text-slate-600">{t('web.doctorImport.hint')}</p>
      {error && <p role="alert" className="text-red-700">{error}</p>}
      <div className="flex flex-wrap items-center gap-3">
        <input type="file" accept=".csv,text/csv" aria-label={t('web.doctorImport.file')} disabled={busy} onChange={(e) => void onFile(e)} />
        <button className="rounded border px-2 py-1" onClick={() => downloadText('doctors-template.csv', '﻿' + doctorsTemplateCsv, 'text/csv;charset=utf-8')}>
          {t('web.doctorImport.template')}
        </button>
      </div>
      {checked && checked.errors.length > 0 && (
        <div>
          <p role="alert" className="text-red-700">{t('web.doctorImport.errors', { count: String(checked.errors.length) })}</p>
          <ul className="list-disc ps-5">
            {checked.errors.slice(0, 50).map((x, i) => (
              <li key={i}>{t('web.doctorImport.error', { row: String(x.row), field: x.field, code: x.code })}</li>
            ))}
          </ul>
        </div>
      )}
      {clean && rows && (
        <div className="flex flex-wrap items-center gap-3">
          <span>{t('web.doctorImport.checked', { created: String(checked.created), duplicates: String(checked.duplicates) })}</span>
          <button className="rounded border px-3 py-1" disabled={busy || checked.created === 0} onClick={() => void run(false, rows)}>
            {t('web.doctorImport.import')}
          </button>
        </div>
      )}
      {done && <p>{t('web.doctorImport.done', { created: String(done.created), duplicates: String(done.duplicates) })}</p>}
    </div>
  )
}
