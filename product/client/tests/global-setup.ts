import { request } from '@playwright/test'
import { apiBase, showcaseSeed } from './auth'

export default async function globalSetup(){
  if(process.env.AEROLINK_E2E_SKIP_SHOWCASE_SEED==='true')return
  const context=await request.newContext({baseURL:apiBase})
  try{
    const showcase=await showcaseSeed(context)
    process.env.AEROLINK_SHOWCASE_SEED=JSON.stringify(showcase)
  }finally{
    await context.dispose()
  }
}
