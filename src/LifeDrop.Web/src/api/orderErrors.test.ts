import { describe, expect, it } from 'vitest'
import { ApiError, errorMessage } from './admin.ts'

describe('answers of an order that the panel shows (step 5.71)', () => {
  it('says which warehouse problem it is instead of "already changed"', () => {
    expect(errorMessage(new ApiError(409, 'NO_CITY_WAREHOUSE'))).toContain('Set the warehouse of the city')
    expect(errorMessage(new ApiError(409, 'WAREHOUSE_NOT_ALLOWED'))).toContain("not the warehouse of the customer's city")
    expect(errorMessage(new ApiError(409, 'SOMETHING_ELSE'))).toContain('already changed')
  })
})
