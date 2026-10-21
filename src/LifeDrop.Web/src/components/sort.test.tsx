import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { SortTh, useSort } from './sort.tsx'

const rows = [{ n: 'b', c: 2 }, { n: 'a', c: 10 }, { n: '', c: 1 }]

function Table() {
  const s = useSort(rows, { n: (r) => r.n, c: (r) => r.c })
  return (
    <table>
      <thead><tr><SortTh id="n" sort={s.sort} onSort={s.toggle}>Name</SortTh><SortTh id="c" sort={s.sort} onSort={s.toggle}>Count</SortTh></tr></thead>
      <tbody>{(s.rows ?? rows).map((r, i) => <tr key={i}><td>{r.n || '-'}</td><td>{r.c}</td></tr>)}</tbody>
    </table>
  )
}

const names = () => screen.getAllByRole('row').slice(1).map((r) => r.firstChild?.textContent)

describe('sortable headers', () => {
  it('cycles ascending, descending and original order, empty values last', async () => {
    render(<Table />)
    expect(names()).toEqual(['b', 'a', '-'])
    await userEvent.click(screen.getByRole('button', { name: /Name/ }))
    expect(names()).toEqual(['a', 'b', '-'])
    await userEvent.click(screen.getByRole('button', { name: /Name/ }))
    expect(names()).toEqual(['b', 'a', '-'])
    await userEvent.click(screen.getByRole('button', { name: /Name/ }))
    expect(names()).toEqual(['b', 'a', '-'])
    await userEvent.click(screen.getByRole('button', { name: /Count/ }))
    expect(names()).toEqual(['-', 'b', 'a'])
  })
})
