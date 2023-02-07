import { Button, Table, Typography } from '@equinor/eds-core-react'
import { Mission } from 'models/Mission'
import { MissionStatusDisplay } from './MissionStatusDisplay'
import { useNavigate } from 'react-router-dom'
import { format } from 'date-fns'

interface IndexedMissionProps {
    index: number
    mission: Mission
}

interface MissionProps {
    mission: Mission
}

function MissionEndTimeDisplay({ mission }: MissionProps) {
    return (
        <>
            {new Date(mission.endTime!).getFullYear() !== 1 ? (
                <Typography>{format(new Date(mission.endTime!), 'HH:mm:ss - dd/MM/yyyy')}</Typography>
            ) : (
                <Typography>-</Typography>
            )}
        </>
    )
}

export function PastMissionCard({ index, mission }: IndexedMissionProps) {
    let navigate = useNavigate()
    const routeChange = () => {
        let path = '/robotics-frontend/mission/' + mission.id
        navigate(path)
    }
    return (
        <Table.Row key={index}>
            <Table.Cell>
                <MissionStatusDisplay status={mission.missionStatus} />
            </Table.Cell>
            <Table.Cell>
                <Button as={Typography} variant="ghost" onClick={routeChange}>
                    {mission.name}
                </Button>
            </Table.Cell>
            <Table.Cell>
                <MissionEndTimeDisplay mission={mission} />
            </Table.Cell>
        </Table.Row>
    )
}