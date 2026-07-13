import { useEffect, useState } from 'react'
import './PersonPicker.css'

type Person={id:string;userName:string;displayName:string;email:string;title:string;roles:string[]}
export default function PersonPicker({api,projectId,value,name,index,onSelect}:{api:string;projectId:string;value:string;name:string;index:number;onSelect:(person:{userId:string;name:string})=>void}){
 const [query,setQuery]=useState(name||value),[people,setPeople]=useState<Person[]>([]),[open,setOpen]=useState(false)
 useEffect(()=>{const timer=setTimeout(async()=>{if(query.trim().length<2){setPeople([]);return}const response=await fetch(`${api}/api/directory?projectId=${projectId}&search=${encodeURIComponent(query)}&limit=10`);if(response.ok)setPeople(await response.json())},150);return()=>clearTimeout(timer)},[api,projectId,query])
 return <div className="personPicker"><input aria-label={`Approver ${index+1} search`} value={query} placeholder="Search name, username, title, or role…" autoComplete="off" onFocus={()=>setOpen(true)} onChange={event=>{setQuery(event.target.value);setOpen(true);onSelect({userId:'',name:''})}}/>{value&&<small>{value}</small>}{open&&people.length>0&&<div className="personSuggestions">{people.map(person=><button type="button" key={person.id} onClick={()=>{onSelect({userId:person.userName,name:person.displayName});setQuery(person.displayName);setOpen(false)}}><i>{person.displayName.split(' ').map(x=>x[0]).join('').slice(0,2)}</i><span><b>{person.displayName}</b><small>{person.title} · {person.userName}</small></span><em>{person.roles.join(' · ')}</em></button>)}</div>}</div>
}
